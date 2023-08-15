# Timeular Tracker - track where you want
I have been using a Timeular Tracker Bluetooth Low Energy device from when it first came out a couple of years ago.

Unfortunatly a paid subscription is now required to use the device (which I only discovered when I wanted to gift one of my trackers to my brother).
This code frees your tracker of the need to have a timeular.com account and paid subscription.

Don't expect a fancy frontend, the app is a Windows CLI application that searches for the first Timeluar device it can connect to. It then subscribes to the
"orientation changed" event and triggers an action. As a sample application I use the free kimai.org time tracking, but the code can be easily extended to
trigger any other application.

## Configuration for kimai.org

On first Start the *timular.json* file will be copied to the user's *Documents* folder.

Please edit this file and add **api_host**, **api_user**, **api_key** values which can be found in the user management of Kimai.

The **sides** array assigns Kimai project IDs to each side

    => Resting in its base = index 0 and index 10
    => Side 1              = index 1 = example project ID 456
    => Side 2              = index 2 = example project ID 145
    ... etc.

The **default_activity_id** is a required attribute when starting an activity in Kimai. It should be the same in all projects you want to track.
I usually create a *Timeular Default* activity as first activity when a project is created in Kimai.

Anything tracked by this application will always use this activity ID, no matter which project gets tracked!
