![GitHub Release](https://img.shields.io/github/v/release/hexbyt3/Svraidbot)
![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/hexbyt3/Svraidbot/total?color=violet)


# Sc/Vi RaidBot

![Discord Banner 2](https://discord.com/api/guilds/1369342739581505536/widget.png?style=banner2)
 
## Hello, and welcome to my RaidBot Project.

![image](https://github.com/user-attachments/assets/9d9b5244-9403-420d-8099-c6579b503073)
![image](https://github.com/user-attachments/assets/023ba97e-0189-435d-9271-c7b3a1be7e54)
![image](https://github.com/user-attachments/assets/cba6f9c1-ee80-4ef2-9853-527ae06e9a23)


# How-To Video
https://youtu.be/wlFE04oiqGs?si=EDoVRaocGyCapN9n


# 📱 Access SVRaidBot from Any Device on Your Network

![image](https://github.com/user-attachments/assets/cc4eb0f2-f3f7-4ee7-82f2-b771f6fbbd56)

## Quick Setup

### 1. Enable Network Access (choose one):
- **Option A:** Right-click SVRaidBot.exe → Run as Administrator
- **Option B:** Run in admin cmd: `netsh http add urlacl url=http://+:9090/ user=Everyone`

### 2. Allow Through Firewall:
Run in admin cmd:
```cmd
netsh advfirewall firewall add rule name="SVRaidBot Web" dir=in action=allow protocol=TCP localport=9090
```

### 3. Connect From Your Phone:
- Get your PC's IP: `ipconfig` (look for IPv4 Address)
- On your phone: `http://YOUR-PC-IP:9090`
- Example: `http://192.168.1.100:9090`

## Requirements
- Same WiFi network
- Windows Firewall rule (step 2)
- Admin rights (first time only)

---


# __Features__
## Auto Teleport 
- Teleports user to the nearest raid den if raid den is lost.
## Raid Requests
__Adding Requests__
 - Users can request their own raids using command `ra <seed> <difficulty> <storyprogress>`.
 - I have an online seed finder located here for your users to use: [https://genpkm.com/raids/seeds/](https://genpkm.com/raids/seeds/index.php)
   
__Removing Request__
- Users can remove thier raid request by simply typing the `rqc` command.  This removes them from the queue.
  
__RaidView__
- Users can view a raid embed with all the details of the raid including stats, rewards, and more!  Command `rv <seed> <difficulty> <storyprogress>`
  
__Disable Requests__
- The bot owner can enable or disable requesting raids at the click of a button.
  
__Limit Requests__
 - The bot owner can decide how many requests a user can submit in a certain time limit if they wish.
   
## Full Event Support
- Users can request an active event they want.  The bot will auto teleport your player to the nearest Event den and proceed with overwriting the seed.
- Auto Detects Might and Distribution Group ID's to be used. 
## Mystery Raids
- Don't have time to come up with your own raids to add to the list?  Turn on the Mystery Raids function and get a random shiny raid each time!
## Disable Overworld Spawns
- Tired of your player getting in random battles with overworld pokemon?  Turn this feature on and there will be no more overworld spawns to deal with!
## Embed Settings
- Tons of embed settings for the bot owner to choose from.  Customize it your way!
## Auto Story Progress 
- StoryProgress will automatically edit the game flags to change the story progress for you per raid!  No need for two bots anymore for "baby" raids and "adult" raids!  

# __SV Raid Bot Guide__

 - **ActiveRaids**
  - Changing Battle Pokémon - Inside of the collection editor is where all of your requested and auto rotate Pokémon live.  You can change the bots Pokémon by editing the `PartyPK` setting and opening up the editor.  Here, you will put in your desired showdown format for the Pokémon you wish the bot to use for the raid.  The bot will use that Pokémon for that raid only.  Once the raid is done, the original Pokémon that you had first in your party will be used in the next raid unless the next raid also has a PartyPK filled out.
  - New 4/27/25 - When starting the program for the first time, the bot will automatically add two random shiny raids to your ActiveRaids list.  That way, you don't have to figure out how to add raids and can start right away, especially if you turn the Mystery Raid feature on.  This makes the program a lot more beginner friendly.
- **RaidSettings**
 - GenerateRaidsFromFile - Set this to `True` and add your own seeds that you want rotated to this file.  When you start up the program for the first time, it will create a new folder called `raidfilessv`    for you and add the `raidsv.txt` file to it.  You will open this file, and add your seeds to it like this `<seed>-<speciesname>-<stars/difficulty>-<storyprogresslevel>`
   - Example:  If i'm looking at Raidcalc and your settings were  Story Progress: 4* Unlocked and Stars: 3, you would add that seed in as `3739A70B-Goomy-3-4`
   - As of 10/25/23 I include two templates for you in the folder - paldeaseeds.txt and kitakamiseeds.txt - you can copy those seeds and add to your raidsv.txt file as a starting point.
  - Save `raidsv.txt` with your new changes.
  - Start NotRaidBot and the list from raidsv.txt will now begin to populate the list inside of the setting `ActiveRaids`.  
 - SaveSeedsToFile - Set to true so that the bot saves a back up of your current ActiveRaids so you can paste them back to raidsv.txt if you ever need to.
 - RandomRotation - Set to true if you want the bot to do random raids in your ActiveRaids list while also keeping Requested raids a priority.
 - MysteryRaids - Set to true for the bot to randomly inject a shiny raid.  Cannot be used with RandomRotation on.
 - DisableRequests - Disables users from being able to request raids.
 - DisableOverworldSpawns - Stops wild pokemon from spawning in overworld if set to true.  Set back to false to make pokemon spawn.
 - KeepDaySeed - Set this to True so that the bot will inject the correct Today Seed if it rolls over to tomorrow.
 - EnableTimeRollback - Set to true for bot to roll time back 5 hours to prevent the day from changing.

- **Embed Toggles**
 - RaidEmbedDescription - add any text you want to show on *all* embeds posted at the top.  
 - SelectedTeraIconType - This changes the icons used in your embed.  Icon1 are custom tera icons that look amazing.
 - IncludeMoves - set to true if you want to show the moves the raid mon will have in the embed.
 - IncludeRewards - set to true if you want to show the rewards the raid mon will have in the embed.
 - IncludeSeed - Set to true to show the current raid seed in the embed.
 - IncludeCountdown - Set to true to show time until Raid starts in embed.
 - IncludeTypeAdvantage - Set to true to show super effective types in embed.
 - RewardsToShow - A list of rewards you want to show on your embeds.
 - RequestEmbedTime - Time to wait to post user requested raids to public channel.
 - TakeScreenShot - Set to true to show screenshot of the game in your embeds.
 - ScreenshotTiming - Set to 1500ms or 22000ms to take different screenshots once in raid.
 - HideRaidCode - Hides raid code from embed.

- **EventSettings**
 - EventActive - Set to true if Event is active (Might or Distribution).  The bot will auto set this if Event is detected.
 - RaidDeliveryGroupID - The Event Index of the current Event goes here.  The bot will auto set this if Event is detected. 

- **LobbyOptions**
 - LobbyMethod
  -  SkipRaid and it will just skip the raid if it's empty after the specified times defined in `SkipRaidLimit`
  - Continue - will just continue posting the same raid until someone joins.  
  - OpenLobby - will open the lobby as Free For All after X amount of Empty Lobbies.
 - Action - Set this to `MashA` so that the bot presses "A" in the game every 3.5s in battle.  `AFK` means the bot will not do anything.
 - ExtraTimeLobbyDisband - once lobby is disbanded, add extra time to return to overworld if needed.  
 - ExtraTimePartyPK - Extra time to wait to switch your lead raid mon to battle with.  Slow switches only.

- **RaiderBanList**
 - List - This is where all NID's of banned people go.  Use `ban <playername or NID>` to ban or add them manually here.
 - AllowIfEmpty - Keep false.

- **MiscSettings**
 - DateTimeFormat - Set the time format as it appears on your switch. 
 - UseOvershoot - If true, bot will use overshoot method instead of pressing DDown to get to date/time settings.  If true, be sure to ConfigureRolloverCorrection.
 - DDownClicks - Times bot needs to press DDown to get to Date/Time Settings.
 - ScreenOff - Turns your screen off while playing to preserve LED/Power.   Or use commands `screenOff` or `screenOn`.
- **DiscordSettings**
- Token - Add your discord bot token you got from the [Discord Developer Portal](<https://discord.com/developers/applications/>)
- CommandPrefix - the prefix your bot will use for commands.  Common is $
- RoleSudo - Tell the bot who it's daddy (or mommy) is.  Go to your server in a channel the bot has permission to read and type `$sudo @YOURUSERNAME`.  The bot is now under your command.
- ChannelWhitelist - these are channels that you want your bot to listen to commands in.  Use `$addchannel` to add a channel to the bot automatically.
- LoggingChannels - if you want to log all the stuff your bot puts in the Log Tab of the program but in a channel, use the `$loghere` command.
- EchoChannels - These are channels you want your raid embeds to post to.  Use command `$aec` to add the channel to this list.

## __Announcement Settings__

This is helpful if your bot is in several servers and you need to let everyone know that's using it that the bot is online, offline, napping, etc. without having to send out tons of messages yourself.  Just use the `$announce TEXT HERE`command to send out a nice announcement wrapped in a beautiful embed with your choice of thumbnail image and color.
- AnnouncementThumbnailOption - Set this to your fave pokemon image that i've premade.
- CustomAnnouncementThumbnailURL - Put the url to your own thumbnail image if you don't like mine.
- AnnouncementEmbedColor - Self explanatory.
- RandomAnnouncementThumbnail - set to true if you want it to use random images from my custom thumbnails.  Does not work if you have a custom image you're using.
- RandomAnnouncementColor - Let the bot choose from the list what color the embed will be this time.

# __In-Game Set Up__
- Stand in front of your raid crystal
- In game Options, make sure of the following:
 -  `Give Nicknames` is Off
 - `SendToBoxes` is `Automatic`(in case bot catches raid mon)
 - Auto Save is `Off`
 - Text Speed is `Fast`
- Start the bot

## __Program Setup__
- Enter raid description as you like or leave it blank
- To post raid embeds in a specific channel use the `aec` command.
- Paste your raid's seed in the Seed parameter.
- **Code the raid**
 - Set to true if you want a coded raid
- **TimeToWait**
 - Total time to wait before attempting to enter the raid.
- **DateTimeFormat**
 - Set the proper date/time format in your settings for when its time to apply rollover correction.
- **TimeToScrollDownForRollover**
 - For this you want to OVERSHOOT the Date/Time setting
 - ~800 is for Lites, ~950 for OLEDs, and ~920 for V1s
 - Time will vary for everyone.
- **ConfigureRolloverCorrection**
 - If true will only run the rollovercorrection routine for you to figure out your timing
 - Run this when the game is closed.


# All of my Projects

## Showdown Alternative Website
- https://genpkm.com - An online alternative to Showdown that has legality checks and batch trade codes built in to make genning pokemon a breeze.
  
## PKHeX - AIO (All-In-One)

- [PKHeX-AIO](https://github.com/bdawg1989/PKHeX-ALL-IN-ONE) - A single .exe with ALM, TeraFinder, and PokeNamer plugins included.  No extra folders and plugin.dll's to keep up with.

## MergeBot - The Ultimate TradeBot

- [Source Code](https://github.com/bdawg1989/SysBot)

---

# 🚀 Universal Bot Controller

This RaidBot project now supports the **Universal Bot Controller System**, enabling management of both RaidBot and PokeBot through a single web interface.

## ✨ Features

### 🔄 Master-Slave Architecture
- First started bot instance becomes the **Master** (Web server on port 8080)
- Additional instances are automatically detected as **Slaves**
- Works with any combination of RaidBot and PokeBot

### 🌐 Unified Web Interface
- Access via `http://localhost:8080/` regardless of which bot starts first
- Visually distinguishable bot types:
  - ⚔️ **RaidBot** (purple highlighted)
  - 🎮 **PokeBot** (green highlighted)
- Automatic bot type detection and display

### ⚡ Universal Commands
- **Start All** - Starts all bots of all types
- **Stop All** - Stops all bots of all types
- **Idle All** - Sets all bots to idle mode
- **Refresh Map All** - RaidBot-specific function for all RaidBot instances

### 🗺️ RaidBot-Specific Features
- **Refresh Map** button only visible for RaidBot instances
- Automatic raid map refresh across all RaidBot instances
- RaidBot-specific update system

### 🔧 Bot-Specific Features
- Separate update systems for RaidBot and PokeBot from their respective repositories
- Bot-specific functions are automatically enabled/disabled
- Intelligent command routing based on bot type

## 🚀 Usage

### Single RaidBot:
1. Start `SVRaidBot.exe`
2. Open `http://localhost:8080/` (Port changed from 9090 to 8080 for compatibility)
3. Manage your RaidBot instances

### Mixed Bot Environment:
1. Start any combination of RaidBot and PokeBot
2. The first started bot takes over the web server role
3. All subsequent bots are automatically detected
4. Manage all bots through a single interface at `http://localhost:8080/`

### Network Access for Universal Controller:
```cmd
# Admin permission for all bot types (Port 8080 instead of 9090)
netsh http add urlacl url=http://+:8080/ user=Everyone

# Firewall rule for Universal Controller (Port 8080 instead of 9090)
netsh advfirewall firewall add rule name="Universal Bot Controller" dir=in action=allow protocol=TCP localport=8080
```

## 🔧 Technical Details

### Port Standardization
- **IMPORTANT**: RaidBot now uses **Port 8080/8081** instead of 9090/9091 for compatibility
- Automatic collision detection and master-slave assignment
- Cross-bot communication via port files

### Bot Type Detection
- Automatic detection via Reflection:
  - RaidBot: `SysBot.Pokemon.SV.BotRaid.Helpers.SVRaidBot`
  - PokeBot: `SysBot.Pokemon.Helpers.PokeBot`

### Update Management
- RaidBot: Updates from RaidBot repository
- PokeBot: Updates from PokeBot repository
- Automatic repository detection based on bot type

### RaidBot-Specific Functions
- Refresh Map functionality is preserved
- Automatic detection and activation of raid-specific features
- Compatibility with all existing RaidBot configurations

## 📱 Mobile Access Update

Since the port changed from 9090 to 8080, now use:
- **New URL**: `http://YOUR-PC-IP:8080` (instead of 9090)
- **Example**: `http://192.168.1.100:8080`

The Universal Bot Controller System makes managing multiple bot instances of different types easier than ever! 🎉

---

# 🌐 Tailscale Multi-Node Management

Take your bot management to the next level with **Tailscale** integration! Manage bot instances across multiple computers/servers through a single web interface.

## ✨ What is Tailscale Integration?

Tailscale allows you to create a secure mesh network between your devices, enabling the Universal Bot Controller to manage bots running on different computers across the internet as if they were on the same local network.

### 🔑 Key Benefits
- **Remote Management**: Control bots on multiple computers from one dashboard
- **Secure**: Encrypted mesh network via Tailscale
- **Automatic Discovery**: Master node automatically finds and manages remote bots
- **Load Distribution**: Run bots on multiple machines for better performance
- **Geographic Distribution**: Place bots closer to different regions

## 🚀 Setup Guide

### 1. Install Tailscale
1. Install [Tailscale](https://tailscale.com/) on all computers you want to connect
2. Log in with the same Tailscale account on all devices
3. Note the Tailscale IP addresses of each device (usually `100.x.x.x`)

### 2. Configure Master Node
The **Master Node** runs the web dashboard and manages all other nodes:

```json
{
  "Hub": {
    "Tailscale": {
      "Enabled": true,
      "IsMasterNode": true,
      "MasterNodeIP": "100.x.x.x",
      "RemoteNodes": [
        "100.x.x.x",
        "100.x.x.x"
      ],
      "PortScanStart": 8081,
      "PortScanEnd": 8110,
      "PortAllocation": {
        "Enabled": true,
        "NodeAllocations": {
          "100.x.x.x": { "Start": 8081, "End": 8090 },
          "100.x.x.x": { "Start": 8091, "End": 8100 },
          "100.x.x.x": { "Start": 8101, "End": 8110 }
        }
      }
    }
  }
}
```

### 3. Configure Slave Nodes
**Slave Nodes** run bots and report to the master:

```json
{
  "Hub": {
    "Tailscale": {
      "Enabled": true,
      "IsMasterNode": false,
      "MasterNodeIP": "100.x.x.x",
      "RemoteNodes": [],
      "PortScanStart": 8091,
      "PortScanEnd": 8100
    }
  }
}
```

## ⚙️ Configuration Details

### Tailscale Settings

| Setting | Description | Master Node | Slave Node |
|---------|-------------|-------------|------------|
| `Enabled` | Enable Tailscale integration | `true` | `true` |
| `IsMasterNode` | Is this the master dashboard? | `true` | `false` |
| `MasterNodeIP` | Tailscale IP of master node | Own IP | Master's IP |
| `RemoteNodes` | List of slave node IPs | All slave IPs | `[]` (empty) |
| `PortScanStart` | Start of port scan range | `8081` | Your range start |
| `PortScanEnd` | End of port scan range | `8110` | Your range end |

### Port Allocation System

The port allocation system prevents conflicts when running multiple bot instances across nodes:

```json
"PortAllocation": {
  "Enabled": true,
  "NodeAllocations": {
    "100.x.x.x": { "Start": 8081, "End": 8090 },  // Master: 10 ports
    "100.x.x.x": { "Start": 8091, "End": 8100 },  // Slave 1: 10 ports  
    "100.x.x.x": { "Start": 8101, "End": 8110 }    // Slave 2: 10 ports
  }
}
```

## 🎯 Usage Examples

### Basic 2-Node Setup
**Computer 1 (Master)** - Main gaming PC:
- Tailscale IP: `100.x.x.x`
- Runs web dashboard on port 8080
- Manages 5 local bots on ports 8081-8085

**Computer 2 (Slave)** - Dedicated server:
- Tailscale IP: `100.x.x.x`  
- Runs 10 bots on ports 8091-8100
- No local web interface needed

**Access**: Go to `http://100.x.x.x:8080` from any device on your Tailscale network

### Advanced Multi-Node Setup
**Server Farm Configuration**:
- **Master Node**: Dashboard + 5 bots (ports 8081-8085)
- **Slave Node 1**: 10 PokeBot instances (ports 8091-8100)
- **Slave Node 2**: 10 RaidBot instances (ports 8101-8110)
- **Slave Node 3**: Additional capacity (ports 8111-8120)

## 🔧 Network Requirements

### Firewall Configuration
Each node needs appropriate firewall rules:

```cmd
# Allow Tailscale traffic (usually automatic)
# Allow bot TCP ports
netsh advfirewall firewall add rule name="Bot Ports" dir=in action=allow protocol=TCP localport=8081-8110

# Master node also needs web server port
netsh advfirewall firewall add rule name="Bot Dashboard" dir=in action=allow protocol=TCP localport=8080
```

### Port Planning
- **Web Dashboard**: 8080 (master node only)
- **Bot Instances**: 8081+ (configurable ranges per node)
- **Tailscale**: Automatic (usually UDP 41641)

## 📊 Dashboard Features

### Multi-Node View
The master dashboard displays all nodes with:
- **Node Status**: Online/Offline indicators
- **Bot Counts**: Total bots per node
- **Performance**: Individual bot statuses
- **Commands**: Send commands to specific nodes or all nodes

### Global Commands
- **Start All**: Starts bots on all connected nodes
- **Stop All**: Stops bots across the entire network
- **Update All**: Updates bot software on all nodes
- **Restart All**: Restarts all bot instances network-wide

## 🔍 Troubleshooting

### Common Issues

**Bots not discovered on remote nodes:**
1. Verify Tailscale connectivity: `ping 100.x.x.x`
2. Check firewall rules on target node
3. Ensure port ranges don't overlap
4. Verify JSON configuration syntax

**Connection timeouts:**
1. Check if ports are in use: `netstat -an | findstr :8081`
2. Verify bot is actually running on expected port
3. Test manual connection: `telnet 100.x.x.x 8081`

**Master node not starting:**
1. Ensure `IsMasterNode: true` is set
2. Check that port 8080 is available
3. Verify admin permissions for network access

### Debug Information
Enable verbose logging to troubleshoot:
- Check Windows Event Logs
- Monitor bot console output
- Use `telnet` to test TCP connectivity
- Verify Tailscale status with `tailscale status`

## 🚨 Security Considerations

### Network Security
- Tailscale provides encrypted mesh networking
- Only devices on your Tailscale network can access the dashboard
- Consider using Tailscale ACLs for additional security
- Regularly review connected devices in Tailscale admin panel

### Access Control
- Web dashboard has no built-in authentication
- Rely on Tailscale network-level security
- Consider additional reverse proxy with authentication if needed
- Monitor access logs for suspicious activity

The Tailscale integration transforms your bot network into a powerful distributed system! 🚀

