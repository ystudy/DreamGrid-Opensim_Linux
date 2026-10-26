Welcome to OpenSimulator (OpenSim for short)!

# Overview

OpenSim is a BSD Licensed Open Source project to develop a functioning
virtual worlds server platform capable of supporting multiple clients
and servers in a heterogeneous grid structure. OpenSim is written in
C#, and can run under Mono or the Microsoft .NET runtimes.

This is considered an alpha release.  Some stuff works, a lot doesn't.
If it breaks, you get to keep *both* pieces.

# Install DreamGrid

DreamGrid packages OpenSim with its own database, grid services and an admin
website, and runs them as a service. Each install ends with a link: open it in
your browser to name your world and create your avatar.

## Debian 12 and 13 (amd64)

Run this as a user who can use sudo:

    curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/install.sh | sudo bash

It installs the system packages DreamGrid needs (MariaDB, libgdiplus, ICU),
downloads the latest release and checks it against the release's SHA256SUMS,
creates a `dreamgrid` system user, and installs and starts the `dreamgrid`
systemd service. It then prints the setup link.

## Windows 10 and 11 (x64)

Download `DreamGridSetup-<version>.exe` from the
[latest release](https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest)
and run it. The installer is not signed, so Windows SmartScreen may say
"Windows protected your PC": click **More info → Run anyway**. Allow
DreamGrid through the firewall if Windows asks. The installer sets up the
DreamGrid service and opens the setup link in your browser when it finishes;
the Start menu entry "DreamGrid admin page" opens the admin website later.

## Where things live

| | Debian | Windows |
|---|---|---|
| Your world's data (database, regions, settings, logs) | `/var/lib/dreamgrid` | `C:\ProgramData\DreamGrid` |
| The program | `/opt/dreamgrid` (its .NET runtime: `/opt/dreamgrid-runtime`) | `C:\Program Files\DreamGrid` |

Installing, upgrading and uninstalling never change, move or delete the data
folder.

## Upgrading

Run the same command again on Debian, or the newer installer on Windows. Your
world keeps running while the program is replaced, and the data is kept. The
grid service and region restart briefly when a release updates OpenSim.

## Another web port

The admin website uses port 8001. If another program already uses it, pick
another port from 1024 to 65535 (not one DreamGrid uses itself: 3307, 8002,
8003, 8004, 8010 or 8011):

    curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/install.sh | sudo bash -s -- --web-port 8101

    DreamGridSetup-<version>.exe /WEBPORT=8101

The installer saves the port in your world's settings, so upgrades keep it, and
you can change it later in Advanced Settings → Network.

## Testing a pre-release

Pre-releases (tags such as `v0.1.0-rc1`) are never the latest release, so the
commands above do not pick them up. On Debian, name the tag:

    curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/download/v0.1.0-rc1/install.sh | sudo DREAMGRID_RELEASE=v0.1.0-rc1 bash

On Windows, download `DreamGridSetup-<version>.exe` from that pre-release's
page on the [releases list](https://github.com/ystudy/DreamGrid-Opensim_Linux/releases).

# Compiling OpenSim

Please see BUILDING.md

# Running OpenSim on Windows

You will need the dotnet 10.0 runtime (https://dotnet.microsoft.com/en-us/download/dotnet/10.0)


To run OpenSim from a command prompt

 * cd to the bin/ directory where you unpacked OpenSim
 * review and change configuration files (.ini) for your needs. see the "Configuring OpenSim" section
 * run OpenSim.exe


# Running OpenSim on Linux/Mac

You will need

 * [dotnet 10.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
 * libgdiplus 
 
 if you have mono 6.x complete, you already have libgdiplus, otherwise you need to install it
 using a package manager for your operating system, like apt, brew, macports, etc
 for example on debian:
 
 `apt-get update && apt-get install -y apt-utils libgdiplus libc6-dev`
 
To run OpenSim, from the unpacked distribution type:

 * cd bin
 * review and change configuration files (.ini) for your needs. see the "Configuring OpenSim" section
 * run ./opensim.sh


# Configuring OpenSim

When OpenSim starts for the first time, you will be prompted with a
series of questions that look something like:

	[09-17 03:54:40] DEFAULT REGION CONFIG: Simulator Name [OpenSim Test]:

For all the options except simulator name, you can safely hit enter to accept
the default if you want to connect using a client on the same machine or over
your local network.

You will then be asked "Do you wish to join an existing estate?".  If you're
starting OpenSim for the first time then answer no (which is the default) and
provide an estate name.

Shortly afterwards, you will then be asked to enter an estate owner first name,
last name, password and e-mail (which can be left blank).  Do not forget these
details, since initially only this account will be able to manage your region
in-world.  You can also use these details to perform your first login.

Once you are presented with a prompt that looks like:

	Region (My region name) #

You have successfully started OpenSim.

If you want to create another user account to login rather than the estate
account, then type "create user" on the OpenSim console and follow the prompts.

Helpful resources:
 * http://opensimulator.org/wiki/Configuration
 * http://opensimulator.org/wiki/Configuring_Regions

# Connecting to your OpenSim

By default your sim will be available for login on port 9000.  You can login by
adding -loginuri http://127.0.0.1:9000 to the command that starts Second Life
(e.g. in the Target: box of the client icon properties on Windows).  You can
also login using the network IP address of the machine running OpenSim (e.g.
http://192.168.1.2:9000)

To login, use the avatar details that you gave for your estate ownership or the
one you set up using the "create user" command.

# Bug reports

In the very likely event of bugs biting you (err, your OpenSim) we
encourage you to see whether the problem has already been reported on
the [OpenSim mantis system](http://opensimulator.org/mantis/main_page.php).

If your bug has already been reported, you might want to add to the
bug description and supply additional information.

If your bug has not been reported yet, file a bug report ("opening a
mantis"). Useful information to include:
 * description of what went wrong
 * stack trace
 * OpenSim.log (attach as file)
 * OpenSim.ini (attach as file)


# More Information on OpenSim

More extensive information on building, running, and configuring
OpenSim, as well as how to report bugs, and participate in the OpenSim
project can always be found at http://opensimulator.org.

Thanks for trying OpenSim, we hope it is a pleasant experience.

