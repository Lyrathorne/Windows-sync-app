# Android background connection lifecycle

Status: implemented in P11  
Date: 2026-07-15

## User contract

DeviceSync runs a persistent connection service only after the user enables
**Allow background work**. Android then requires a permanent low-priority notification;
it cannot be hidden while the foreground service is active. Disabling the setting stops
the service and cancels its recovery work.

The service uses `START_STICKY`, but every recreated instance reads the persisted opt-in
before reconnecting. `BOOT_COMPLETED`, package replacement and user unlock start it only
when that opt-in is enabled. Opening the activity alone no longer starts the service.

## Recovery ownership

- `ConnectionManager` remains the only owner of socket reads and writes.
- Connectivity callbacks detect loss and changes between Wi-Fi, hotspot and other
  default networks. A stale authenticated socket is closed before reconnecting.
- Reconnect uses exponential backoff from 2 to 30 seconds plus bounded random jitter.
- A unique WorkManager job is a delayed watchdog only. It may request the foreground
  service to start, but never opens or owns a socket itself.
- Duplicate startup calls and duplicate session cleanup are suppressed.

## Power usage

The connection service takes a partial WakeLock only for the first 30 seconds of a
startup/recovery attempt. It does not hold a permanent Wi-Fi lock. WakeLock and
high-performance Wi-Fi lock are held only while a file transfer is active and are
released when the transfer/service ends.

## OEM and platform limits

Battery optimization and OEM process managers may still stop an ordinary application.
On Infinix/XOS, users should check Phone Master auto-start management, Battery Lab,
background activity and memory-cleanup exclusions. DeviceSync links to the relevant
system application and battery pages but cannot change OEM policy itself.

After Android **Force Stop**, boot receivers, workers and services remain blocked until
the user opens DeviceSync manually. No ordinary application can bypass this restriction.
DeviceSync does not claim guaranteed Microsoft-system-app-level persistence.

## Manual lifecycle matrix

| Scenario | Expected result |
|---|---|
| Activity moved to background | Foreground notification remains; socket and heartbeat continue. |
| Removed from recent apps | Service continues because `stopWithTask=false`; no duplicate restart is issued. |
| Screen locked / Doze | Existing foreground session remains eligible; watchdog provides delayed recovery. |
| Wi-Fi disabled / airplane mode | State becomes waiting for network; no rapid retry loop. |
| Wi-Fi or hotspot changed | Old socket closes; one jittered reconnect cycle starts on the new network. |
| Process killed by system | Sticky service or constrained watchdog restores the opted-in connection. |
| Phone rebooted | Service starts only if background work was explicitly enabled. |
| Force Stop | No automatic restart until DeviceSync is opened manually. |
| Background work disabled | Service stops and unique recovery jobs are cancelled. |

These scenarios require final verification on real Android 8/11/13/14/15+ devices,
including an Infinix/XOS device, because emulator behaviour does not reproduce OEM
battery managers accurately.
