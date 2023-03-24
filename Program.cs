using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using Windows.UI.Notifications;

namespace Timeular
{

    public class jsonConfig
    {
        public string api_user;
        public string api_token;
        public string api_host;
        public string default_activity_id;
        public string[] sides;
    }

    internal class Program
    {

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow([In] IntPtr hWnd, [In] int nCmdShow);
        private static string ORIENTATION_SERVICE = "c7e70010-c847-11e6-8175-8c89a55d403c";
        private static string ORIENTATION_CHARACTERISTICS = "c7e70012-c847-11e6-8175-8c89a55d403c";

        private static int orientation = 0;
        private static Boolean found = false;
        private static Boolean connected = false;
        private static DeviceInformation device;
        private static BluetoothLEDevice bleDevice;

        private static string apiUser;
        private static string apiToken;
        private static string apiHost;
        private static string defaultActivityId;
        private static string[] sides;
        private static HttpClient http;
        private static object lastActivity = null;

        static async Task Main(string[] args)
        {
            // using SpecialFolder.Personal since it's easier to find than ApplicationFolder
            string configPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
            string fileName = "timeular.json";
            string configFileName = Path.Combine(configPath, fileName);

            // check if config file exists
            if (File.Exists(configFileName))
            {
                string jsonString = File.ReadAllText(configFileName);
                jsonConfig settings = JsonConvert.DeserializeObject<jsonConfig>(jsonString);
                apiToken = settings.api_token;
                if( apiToken == "" || apiToken is null)
                {
                    Console.WriteLine("Please edit the the config file: " + configFileName);
                    Console.ReadKey();
                    Environment.Exit(1);
                }
                apiUser = settings.api_user;
                apiHost = settings.api_host;
                defaultActivityId = settings.default_activity_id;
                sides = settings.sides;
            }
            // copy template config file otherwise
            else
            {
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
                File.Copy(fileName, configFileName);
                Console.WriteLine("Please edit the the config file: " + configFileName);
                Console.ReadKey();
                Environment.Exit(1);
            }

            http = new HttpClient();
            // specific Request headers required by Kimai API
            http.DefaultRequestHeaders.Add("X-AUTH-USER", apiUser);
            http.DefaultRequestHeaders.Add("X-AUTH-TOKEN", apiToken);

            // Initialize what's the state in the time tracking application
            GetActiveActivity();
            
            // Prepare the BLE watcher
            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
            DeviceWatcher deviceWatcher = DeviceInformation.CreateWatcher(
                                BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
                                requestedProperties,
                                DeviceInformationKind.AssociationEndpoint);

            // Register event handlers before starting the watcher.
            // Added, Updated and Removed are required to get all nearby devices
            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Updated += DeviceWatcher_Updated;
            deviceWatcher.Removed += DeviceWatcher_Removed;

            // minimize the console window to the task bar
            IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
            ShowWindow(handle, 6);

            // main loop
            while (true)
            {
                // wait for keypress if connected
                //  or search otherwise
                if(!connected)
                {
                    found = false;
                    // Start the watcher until Timeular device has been found
                    deviceWatcher.Start();
                    while (!found)
                    {
                        Console.WriteLine("Scanning ...");
                        // sleep if no device was found (gets populated by "DeviceWatcher_Added" event handler)
                        if (device == null)
                        {
                            Thread.Sleep(1000);
                        }
                        else
                        {
                            // try to connect
                            bleDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                            // register connection status changed event
                            bleDevice.ConnectionStatusChanged += BleDevice_ConnectionStatusChanged;

                            // try to read specific Gatt service uuid (ORIENTATION_SERVICE)
                            GattDeviceServicesResult result = await bleDevice.GetGattServicesForUuidAsync(new Guid(ORIENTATION_SERVICE) );
                            if (result.Status == GattCommunicationStatus.Success)
                            {
                                Console.WriteLine("Found Orientation Service");
                                // try to read specific Gatt charactreistic uuid (ORIENTATION_CHARACTERISTICS)
                                GattCharacteristicsResult res = await result.Services[0].GetCharacteristicsForUuidAsync(new Guid(ORIENTATION_CHARACTERISTICS));
                                if ( res.Status == GattCommunicationStatus.Success)
                                {
                                    Console.WriteLine("Found Orientation Characteristics");
                                    // read current characteristics value (current orientation) as start position of the tracker
                                    GattReadResult r = await res.Characteristics[0].ReadValueAsync();
                                    if (r.Status == GattCommunicationStatus.Success)
                                    {
                                        var reader = DataReader.FromBuffer(r.Value);
                                        int curr_orientation = reader.ReadByte();

                                        // register for Indicate/Notify of that characteristic
                                        GattCommunicationStatus status = await res.Characteristics[0].WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate);
                                        if (status == GattCommunicationStatus.Success)
                                        {
                                            Console.WriteLine("Subscribed to Orientation Changes");
                                            // register handler for orientation changed event
                                            res.Characteristics[0].ValueChanged += Characteristic_ValueChanged;
                                            // Stop the watcher
                                            deviceWatcher.Stop();
                                            // set varables to exit while loop
                                            found = true;
                                            connected = true;

                                            // "orientation" is the state of the time tracker application
                                            // "curr_orientation" is the position of the tracker device
                                            if (orientation != curr_orientation)
                                            {
                                                orientation = curr_orientation;
                                                if (orientation >= 1 && orientation <= 8)
                                                {
                                                    // start tracking with the new tracker device position
                                                    if (sides.Length > orientation && sides[orientation] != "" && sides[orientation] != null)
                                                        StartActivity(sides[orientation], defaultActivityId);
                                                    else
                                                        ShowMessage("Timular", "No task assigned to side: " + orientation.ToString());
                                                }
                                                else
                                                {
                                                    // tracker device is at its base, whatever got tracked by the application is to be stopped
                                                    if (lastActivity != null)
                                                        StopActivity(lastActivity.ToString());
                                                    ShowMessage("Timular", "Not tracking. Tracker is in its base.");
                                                }
                                            }
                                            else
                                            {
                                                // tracker is in it's base and application is not tracking anything
                                                if (orientation == 0 || orientation == 9)
                                                    ShowMessage("Timeular", "Not tracking. Flip your tracker to get started!");
                                                // or GetActivity() already posted the current status
                                                // and the tracker device is in the correct position accordingly
                                            }
                                            Console.WriteLine("Press any key to exit");
                                        }
                                        else
                                        {
                                            // disconnect the device and start over by searching
                                            Console.WriteLine("Error subscribing to device indication: " + result.Status.ToString());
                                            bleDevice.Dispose();
                                            device = null;
                                        }
                                    }
                                    else
                                    {
                                        // disconnect the device and start over by searching
                                        Console.WriteLine("Error reading orientation characteristic's value: " + result.Status.ToString());
                                        bleDevice.Dispose();
                                        device = null;
                                    }
                                }
                            }
                            else
                            {
                                // disconnect the device and start over by searching
                                Console.WriteLine("Error getting device services: " + result.Status.ToString());
                                bleDevice.Dispose();
                                device = null;
                            }
                        }
                    }
                }
                else
                {
                    // main loop waiting for keypress to exit
                    Thread.Sleep(1000);
                    if (Console.KeyAvailable) break;
                }
            }
            bleDevice.Dispose();
        }

        private static void BleDevice_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            // once the connection status changed event fires, change the "connected" variable => handled in Main thread
            connected = !connected;
            Console.WriteLine("Connected: " + connected);
        }
        private static void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            //throw new NotImplementedException();
        }
        private static void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            //throw new NotImplementedException();
        }
        private static void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            // this event is triggered once a new BLE device has been found
            if (args.Name.Contains("Timeular") )
            {
                // if the name conatains the string "Timeular", the "device" varible gets set => continue in Main thread
                Console.WriteLine("Connecting to " + args.Name);
                device = args;
            }
        }
        private static void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            int old_orientation = orientation;
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            orientation = reader.ReadByte();
            if (orientation != old_orientation)
            {
                if (orientation >= 1 && orientation <= 8)
                {
                    // start tracking with the new tracker device position
                    if (sides.Length > orientation && sides[orientation] != "" && sides[orientation] != null)
                        StartActivity(sides[orientation], defaultActivityId);
                    else
                        ShowMessage("Timular", "No task assigned to side: " + orientation.ToString());
                }
                else
                {
                    // tracker device is at its base, whatever got tracked by the application is to be stopped
                    if (lastActivity != null)
                        StopActivity(lastActivity.ToString());
                    ShowMessage("Timular", "Not tracking. Tracker is in its base.");
                }
            }
        }
        public static void ShowMessage(string title, string message)
        {
            // XAML string for displaying message in toast window
            string toastXmlString =
            $@"<toast><visual><binding template='ToastGeneric'><text>{title}</text><text>{message}</text></binding></visual></toast>";

            // create XML from string
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(toastXmlString);
            var toastNotification = new ToastNotification(xmlDoc);

            // display notification
            ToastNotifier toastNotifier = ToastNotificationManager.CreateToastNotifier();
            toastNotifier.Show(toastNotification);
        }
        private static async void GetActiveActivity()
        {
            // get active activity from Kimai
            try
            {
                HttpResponseMessage response = await http.GetAsync(apiHost + "/api/timesheets/active");
                response.EnsureSuccessStatusCode();
                // response is JSON
                var json = await response.Content.ReadAsStringAsync();
                // JToken can take care of objects and arrays
                JToken res = JToken.Parse(json);
                if( res.HasValues )
                {
                    // check if the current activity's project ID is in our list of sides
                    orientation = Array.FindIndex(sides, x => x.ToString() == res[0]["project"]["id"].ToString() );
                    // track the current activity's ID in lastActivity as it's required when stopping/modifying an activity
                    lastActivity = res[0]["id"];
                    ShowMessage("Timeular", "Currently Tracking " + res[0]["project"]["customer"]["name"].ToString());
                }
            }
            catch (HttpRequestException e)
            {
                ShowMessage("Timeular ERROR", "Could not read current activity!!!");
                Console.WriteLine("Error :" + e.Message);
            }
        }
        private static async void StartActivity(string projectId, string activity)
        {
            // create a new activity in Kimai for the project matching the tracker's current side
            string data = "{\"begin\":\"" + DateTime.Now.ToString("s") + "\",\"project\":" + projectId + ",\"activity\":" + activity + "}";
            try
            {
                HttpResponseMessage response = await http.PostAsync(apiHost + "/api/timesheets", new StringContent(data, System.Text.Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                // the activity ID could be read from the output, but calling GetActivity for showing the message about the current project instead
                // ("customer" is not part of the above response)
                GetActiveActivity();
            }
            catch (HttpRequestException e)
            {
                ShowMessage("Timeular ERROR", "Could not start activity!!!");
                Console.WriteLine("Error :" + e.Message);
            }
        }
        private static async void StopActivity(string activity)
        {
            // stop activity in Kimai referenced by lastActivity
            try
            {
                HttpResponseMessage response = await http.GetAsync(apiHost + "/api/timesheets/" + activity + "/stop");
            }
            catch (HttpRequestException e)
            {
                // TODO: in case an activity cannot be stopped due to the duration exceeding the max. allowed duration
                //       the activity needs to be modified with an end-date
                //        PATCH /api/timesheets/{id}
                ShowMessage("Timeular ERROR", "Could not stop activity!!!");
                Console.WriteLine("Error :" + e.Message);
            }
        }
    }
}
