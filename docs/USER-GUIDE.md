# User guide

## Dashboard

The dashboard lists each physical adapter, its address, direct ping, relay RTT, jitter, loss, quality score, current upload/download rate, and role.

- **Quality** is a 0–100 health score derived from availability, latency, jitter, and loss. It is not a percentage of available bandwidth.
- **Download now / Upload now** use Windows adapter byte counters, so background traffic and LinkWeaver tunnel overhead are included.
- **Active: _name_ · verified** means direct-mode Internet was tested through that specific adapter.
- **Tunnel: _paths_** lists only physical paths that recently answered the relay's encrypted probes.

## Preferred connection

**Automatic — best quality** chooses the healthiest direct adapter. Selecting a preferred adapter keeps it active while healthy, immediately falls back when it fails, and waits for repeated stable results before moving back to it. This recovery delay prevents route flapping.

Windows route selection uses interface metrics. LinkWeaver also verifies a switch using a TCP connection explicitly bound to the chosen interface; it does not declare the switch successful from link state alone.

## Modes

| Mode | Behavior | Stable VPS public IP | Uses multiple paths for one flow |
| --- | --- | --- | --- |
| Direct monitoring | Windows uses the selected physical ISP directly. | No | No |
| Bonding | Adaptive packet scheduling over all usable paths. | Yes | Yes |
| Failover | One tunnel path carries payload; all paths receive small health/control probes. | Yes | No, until the active path fails |
| Redundant | Sends the same payload over every healthy path; the first valid copy wins. | Yes | Duplicates rather than aggregates |

Seeing a small amount of traffic on backup adapters in Failover mode is normal. Health probes and control frames are required to know that the backup is ready. Payload traffic is not intentionally split in Failover mode.

## Start bonding

1. Enter the relay's Elastic IP in **Bonding relay**.
2. Press **Setup server** and choose the matching AWS `.pem` file. Wait for the persistent **Server: Ready** state.
3. Choose **Bonding**, **Failover**, or **Redundant**.
4. Press **Start bonding**.
5. Wait for **Bonding: Established** and at least one name beside **Tunnel:**.

One connected adapter is sufficient. LinkWeaver acts as a normal encrypted tunnel until more adapters appear. Newly attached USB tethering or Wi-Fi paths are probed and integrated automatically.

## Choosing a mode

- Use **Bonding** for downloads and maximum aggregate throughput.
- Use **Failover** for gaming when conserving hotspot data matters.
- Use **Redundant** for the shortest practical interruption during a path failure, accepting approximately double transmitted data while two paths are healthy.

No failure detector can guarantee zero lost packets. LinkWeaver probes tunnel paths every 150 ms; the client removes an unanswered path after roughly 600 ms and the relay expires a silent return path after 750 ms. Immediate socket failures are retried on another healthy path.

## History and export

The history page stores up to 24 hours locally. Select Quality, Ping, Download, or Upload and a 15-minute, 30-minute, or 1-hour view. Red markers identify detected drops. Export creates a CSV containing samples and outage events.

## Stopping safely

Press **Stop bonding** before changing relay settings. **Restore defaults** disables LinkWeaver's automatic metric management and restores normal Windows routing. Stopping direct failover can change the public IP; stopping a bonded tunnel ends the stable VPS egress path.
