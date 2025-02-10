# Timeular Tracker - track where you want
I have been using a Timeular Tracker Bluetooth Low Energy device from when it first came out a couple of years ago.

Unfortunatly a paid subscription is now required to use the device (which I only discovered when I wanted to gift one of my trackers to my brother).
This code frees your tracker of the need to have a timeular.com account and paid subscription.

Don't expect a fancy frontend, the app is a Windows CLI application that searches for the first Timeluar device it can connect to. It then subscribes to the
"orientation changed" event and triggers an action. As a sample application I use the free kimai.org time tracking, but the code can be easily extended to
trigger any other application.

## Configuration for kimai.org

On first Start the *timular.json* file will be copied to the user's *Documents* folder.

Please edit this file and add **api_host**, **api_key** values which can be found in the user management of Kimai.

The **sides** array assigns Kimai project IDs and activity IDs to each side, separated by a dot "."

    => Resting in its base = index 0 and index 10
    => Side 1              = index 1 = 1.8 - example project ID 1 and activity ID 8 
    => Side 2              = index 2 = 2.7 - example project ID 2 and activity ID 7
    ... etc.
