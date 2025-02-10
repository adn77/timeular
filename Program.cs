using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

    public class JsonConfig
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
        private static readonly string ORIENTATION_SERVICE = "c7e70010-c847-11e6-8175-8c89a55d403c";
        private static readonly string ORIENTATION_CHARACTERISTICS = "c7e70012-c847-11e6-8175-8c89a55d403c";

        private static int orientation = 0;
        private static Boolean found = false;
        private static Boolean connected = false;
        private static DeviceInformation device;
        private static BluetoothLEDevice bleDevice;

        private static string apiToken;
        private static string apiHost;
        private static string[] sides;
        private static HttpClient http;
        private static string lastTimesheet = null;
        private static DateTime lastStart = DateTime.UtcNow;

        static async Task Main()
        {
            // quit if this program is already running (e.g. a toast message was clicked)
            if (System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly().Location)).Count() > 1) return;

            // using SpecialFolder.Personal since it's easier to find than ApplicationFolder
            string configPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
            string fileName = "timeular.json";
            string configFileName = Path.Combine(configPath, fileName);

            // check if config file exists
            if (File.Exists(configFileName))
            {
                string jsonString = File.ReadAllText(configFileName);
                JsonConfig settings = JsonConvert.DeserializeObject<JsonConfig>(jsonString);
                apiToken = settings.api_token;
                if (apiToken == "" || apiToken is null)
                {
                    Console.WriteLine("Please edit the the config file: " + configFileName);
                    Console.ReadKey();
                    Environment.Exit(1);
                }
                apiHost = settings.api_host;
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
            // Kimai API Authentication
            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiToken);

            // Initialize what's the state in the time tracking application
            GetActiveTimesheet();

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

            GattCharacteristicsResult res;

            // main loop
            while (true)
            {
                // wait for keypress if connected
                //  or search otherwise
                if (!connected)
                {
                    device = null;
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
                            GattDeviceServicesResult result = await bleDevice.GetGattServicesForUuidAsync(new Guid(ORIENTATION_SERVICE));
                            if (result.Status == GattCommunicationStatus.Success)
                            {
                                Console.WriteLine("Found Orientation Service");
                                // try to read specific Gatt charactreistic uuid (ORIENTATION_CHARACTERISTICS)
                                res = await result.Services[0].GetCharacteristicsForUuidAsync(new Guid(ORIENTATION_CHARACTERISTICS));
                                if (res.Status == GattCommunicationStatus.Success)
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
                                            // Stop the watcher
                                            deviceWatcher.Stop();
                                            // set varables to exit while loop
                                            found = true;
                                            connected = true;

                                            // register handler for orientation changed event
                                            res.Characteristics[0].ValueChanged += Characteristic_ValueChanged;
                                            Console.WriteLine("Subscribed to Orientation Changes");

                                            // "orientation" is the state of the time tracking application
                                            // "curr_orientation" is the initial position of the tracker device
                                            CheckOrientationChanged(curr_orientation, orientation);
                                            Console.WriteLine("Press any key to exit");
                                        }
                                        else
                                        {
                                            // disconnect the device and start over by searching
                                            Console.WriteLine("Error subscribing to device indication: " + result.Status.ToString());
                                            bleDevice.ConnectionStatusChanged -= BleDevice_ConnectionStatusChanged;
                                            bleDevice.Dispose();
                                            device = null;
                                        }
                                    }
                                    else
                                    {
                                        // disconnect the device and start over by searching
                                        Console.WriteLine("Error reading orientation characteristic's value: " + result.Status.ToString());
                                        bleDevice.ConnectionStatusChanged -= BleDevice_ConnectionStatusChanged;
                                        bleDevice.Dispose();
                                        device = null;
                                    }
                                }
                            }
                            else
                            {
                                // disconnect the device and start over by searching
                                Console.WriteLine("Error getting device services: " + result.Status.ToString());
                                bleDevice.ConnectionStatusChanged -= BleDevice_ConnectionStatusChanged;
                                bleDevice.Dispose();
                                device = null;
                            }
                        }
                    }
                }
                else
                {
                    // main loop waiting for keypress to exit
                    Thread.Sleep(10000);
                    if (Console.KeyAvailable) break;
                }
            }
            bleDevice.ConnectionStatusChanged -= BleDevice_ConnectionStatusChanged;
            bleDevice.Dispose();
        }

        private static void BleDevice_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            // once the connection status changed event fires, change the "connected" variable => handled in Main thread
            if (sender != null)
            {
                if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    connected = true;
                else
                    connected = false;
                Console.WriteLine("Connected: " + connected);
            }
        }
        private static void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            // this event is triggered once a new BLE device has been found
            if (args.Name.Contains("Timeular"))
            {
                // if the name contains the string "Timeular", the "device" variable gets set => continue in Main thread
                Console.WriteLine("Connecting to " + args.Name);
                device = args;
            }
        }
        private static void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            Debug.WriteLine($"Device updated: {args.Id}");
        }

        private static void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            Debug.WriteLine($"Device removed: {args.Id}");
        }
        private static void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            int old_orientation = orientation;
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            orientation = reader.ReadByte();
            CheckOrientationChanged(orientation, old_orientation);
        }
        private static void CheckOrientationChanged(int current_orientation, int old_orientation)
        {
            if (current_orientation != old_orientation)
            {
                if (current_orientation >= 1 && current_orientation <= 8)
                {
                    // start tracking with the new tracker device position
                    if (sides.Length > current_orientation && sides[current_orientation] != "" && sides[current_orientation] != null)
                        StartTimesheet(sides[current_orientation]);
                    else
                        ShowMessage("Timeular", "No task assigned to side: " + current_orientation.ToString());
                }
                else
                {
                    // tracker device is at its base, whatever got tracked by the application is to be stopped
                    StopTimesheet(lastTimesheet);
                    ShowMessage("Timeular", "Not tracking. Tracker is in its base.");
                }
            }
            else
            {
                // tracker is in it's base and application is not tracking anything
                if (current_orientation == 0 || current_orientation == 9)
                    ShowMessage("Timeular", "Not tracking. Flip your tracker to get started!");
                // or GetActiveTimesheet() already posted the current status
                // and the tracker device is in the correct position accordingly
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
            Console.WriteLine(title + ": " + message);
        }
        private static async void GetActiveTimesheet()
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
                if (res.HasValues)
                {
                    // check if the current activity's project ID is in our list of sides
                    orientation = Array.FindIndex(sides, x => x.ToString() == res[0]["project"]["id"].ToString() + '.' + res[0]["activity"]["id"].ToString() );
                    // track the current timesheet's ID in lastTimesheet as it's required when stopping/modifying an activity
                    lastTimesheet = res[0]["id"].ToString();
                    lastStart = (DateTime)res[0]["begin"];
                    ShowMessage("Timeular", "Currently Tracking " + res[0]["project"]["customer"]["name"].ToString() +": " + res[0]["project"]["name"].ToString() + " (" + res[0]["activity"]["name"].ToString() + ")");
                }
            }
            catch (HttpRequestException e)
            {
                ShowMessage("Timeular ERROR", "Could not read current activity!!!");
                Console.WriteLine("Error :" + e.Message);
            }
        }
        private static async void StartTimesheet(string activity)
        {
            // stop previous activity
            if ( !(lastTimesheet is null) ) { StopTimesheet(lastTimesheet); }

            int retryCount = 0;
            string data;
            HttpResponseMessage response = null;

            // try to start activity up to 3 times -
            // sometimes it fails with "Cannot stop running timesheet" error due to async processing
            while ( ( response is null || !response.IsSuccessStatusCode ) && retryCount < 3)
            {
                if ( retryCount++ > 1)
                {
                    Thread.Sleep(1000);
                }
                string[] ts = activity.Split('.');
                // create a new activity in Kimai for the project matching the tracker's current side
                data = "{\"begin\":\"" + DateTime.Now.ToString("s") + "\",\"project\":" + ts[0] + ",\"activity\":" + ts[1] + "}";
                response = await http.PostAsync(apiHost + "/api/timesheets", new StringContent(data, System.Text.Encoding.UTF8, "application/json"));

                //Console.WriteLine("Debug (" + retryCount.ToString() + "): " + data );

            }
            if ( ! response.IsSuccessStatusCode )
            {
                var json = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Error " + response.StatusCode.ToString() + ": " + json.ToString());
                ShowMessage("Timeular ERROR", "Could not start activity!!! ");
            }
            else
            {
                // the activity ID could be read from the output, but calling GetActiveTimesheet for showing the message about the current project instead
                // ("customer" is not part of the above response)
                GetActiveTimesheet();
            }
        }
        private static async void StopTimesheet(string timesheet)
        {
            if (timesheet != null)
            {
                // if the previous time entry is shorter than 60s, delete it
                if (DateTime.Now.Subtract(lastStart).TotalSeconds < 60)
                {
                    DeleteTimesheet(timesheet);
                }
                else
                {
                    // stop activity in Kimai referenced by timesheet
                    HttpResponseMessage response = await http.GetAsync(apiHost + "/api/timesheets/" + timesheet + "/stop");
                    if (!response.IsSuccessStatusCode)
                    {
                        // sometime a task cannot be stopped, e.g. if the duration exceeded the maximum duration
                        var json = await response.Content.ReadAsStringAsync();
                        JToken res = JToken.Parse(json);
                        res = res.SelectToken("errors.children.duration.errors", errorWhenNoMatch: false);
                        if (res != null)
                        {
                            TimeSpan ts = TimeSpan.Parse("8:00");
                            if (Regex.IsMatch(res[0].ToString(), @"\d{1,2}:\d{2}"))
                            {
                                Match timeString = Regex.Match(res[0].ToString(), @"\d{1,2}:\d{2}");
                                ts = TimeSpan.Parse(timeString.ToString());
                            }

                            // PATCH is not available in this .net core
                            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PATCH"), apiHost + "/api/timesheets/" + timesheet)
                            {
                                Content = new StringContent("{\"end\":\"" + lastStart.Add(ts).ToString("s") + "\"}", System.Text.Encoding.UTF8, "application/json")
                            };
                            response = await http.SendAsync(request);
                            if (!response.IsSuccessStatusCode)
                            {
                                json = await response.Content.ReadAsStringAsync();
                                ShowMessage("Timeular ERROR", "Could not modify previous activity!!!");
                                Console.WriteLine("Error " + response.StatusCode.ToString() + ": " +  json.ToString());
                            }
                            else
                            {
                                ShowMessage("Timeular NOTICE", "Stopping previous activity after " + ts.TotalHours.ToString() + "h");
                            }
                        }
                        else
                        {
                            ShowMessage("Timeular ERROR", "Could not stop activity!!!");
                            Console.WriteLine("Error :" + json.ToString());
                        }
                    }
                }
            }
            // after stopping/deleting an activity unset the lastTimesheet property
            lastTimesheet = null;

        }
        private static async void DeleteTimesheet(string timesheet)
        {
            if (timesheet != null)
            {
                // delete activity in Kimai referenced by timesheet
                HttpResponseMessage response = await http.DeleteAsync(apiHost + "/api/timesheets/" + timesheet);
                if (!response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    ShowMessage("Timeular ERROR", "Could not delete activity!!!");
                    Console.WriteLine("Error " + response.StatusCode.ToString() + ": " + json.ToString());
                }
            }
        }
    }
}

