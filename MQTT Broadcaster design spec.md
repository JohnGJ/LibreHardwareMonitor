# MQTT Broadcaster Design (LibreHardwareMonitor fork)

## Purpose
Add an MQTT publishing feature directly into the LibreHardwareMonitor WinForms app, implemented as a Utility class similar to `Utilities/HttpServer.cs`.

The broadcaster:
- is enabled/disabled via the Options menu (like “Remote Web Server”)
- stores configuration in standard LHM settings
- publishes on the existing sensor refresh cadence (Update Interval)
- publishes only sensors explicitly selected via the sensor context menu (“Broadcast via MQTT”)

Non-goals:
- No separate service / daemon
- No separate “LHMozzie” project
- No new update interval setting


## Repo placement and ownership

### File location
- `LibreHardwareMonitor/Utilities/MqttBroadcaster.cs` (sibling to `HttpServer.cs`)

### Lifetime owner
- `MainForm` owns a single instance of the broadcaster (same pattern as `HttpServer`):
  - constructed during `MainForm` init
  - start/stop controlled by a `UserOption` bound to a menu item
  - disposed/stopped on shutdown (e.g., in `CloseApplication()`)

### Publish cadence
- Do **not** introduce a timer.
- Publishing is triggered from the existing update pipeline:
  - `Timer_Tick()` -> background updater -> `_computer.Accept(_updateVisitor)` refreshes sensors
  - after refresh completes (or at the appropriate existing hook), call publisher with current selection
- Publishing must not block the refresh pipeline (async / fire-and-forget with drop behavior).


## User experience (UI)

### Options menu
Add a new section under Options mirroring “Remote Web Server”:

- `Options -> MQTT Broadcaster -> Run` (checkable)
- `Options -> MQTT Broadcaster -> Settings…`

#### Run toggle behavior
- When checked: Start the MQTT broadcaster client (connect / maintain connection).
- When unchecked: Stop and disconnect cleanly.
- Persisted via a `UserOption` (like `_runWebServer`).

#### Settings dialog
A simple dialog (similar in complexity to `InterfacePortForm` / `AuthForm`) allowing editing of:
- Host, Port
- TLS on/off (optional if you want to keep MVP smaller)
- Username/Password (optional)
- Base Topic
- QoS, Retain

Validation:
- Host must not be empty
- Port must be 1–65535

### Sensor context menu
Add a new sensor-level item alongside existing items:

- `Broadcast via MQTT` (checkable)

This should be implemented where the context menu for sensors is constructed (same area that creates “Show in Tray” and “Show in Gadget”).

Persistence format must match existing tray/gadget patterns:
- Use a per-sensor boolean key:
  - `new Identifier(sensor.Identifier, "mqtt").ToString()`

This yields keys like:
- `<sensorIdentifier>/mqtt`

Exactly like existing:
- `<sensorIdentifier>/tray`
- `<sensorIdentifier>/gadget`


## Settings & persistence

### Persistent settings keys (recommended)
Follow the same Settings mechanism used by the Remote Web Server keys (`listenerIp`, `listenerPort`, etc.) and tray/gadget identifier keys.

#### Global MQTT config
- `mqttHost` (string) default: `localhost`
- `mqttPort` (int) default: `1883`
- `mqttUseTls` (bool) default: `false` (optional)
- `mqttUsername` (string) default: empty (optional)
- `mqttPassword` (string) default: empty (optional)
- `mqttBaseTopic` (string) default: `librehardwaremonitor`
- `mqttClientId` (string) default: empty => auto-generate
- `mqttQos` (int) default: `0` (0/1/2)
- `mqttRetain` (bool) default: `true`
- `mqttPublishJson` (bool) default: `true` (optional)

#### Enable/run toggle
- handled by `UserOption` bound to the menu item (like `runWebServerMenuItem`), rather than a raw settings key.
- if LHM stores the web server run toggle via `UserOption`, do the same for MQTT.

#### Per-sensor selection
- boolean setting per sensor:
  - `Identifier(sensor.Identifier, "mqtt").ToString()` -> `true/false`
- removal: delete key (or set false) consistent with how tray/gadget handles removal.

### Selection semantics
- Independent selection list: MQTT selection is separate from tray/gadget.
- Only sensors with `<sensorIdentifier>/mqtt == true` are published.


## MQTT client behavior

### Library choice
- MQTTnet (4.x)

### Connection model
- One client instance per app.
- Start() initiates connection (async) and keeps it alive.
- Stop() cancels reconnect loop and disconnects.

### Reconnect strategy
- On connect failure or disconnect:
  - exponential backoff (e.g., 1s, 2s, 5s, 10s, 30s, 60s max)
- Must not spam logs: log first failure, then periodic “still failing” messages.

### Publish strategy
Publishing is invoked on each update tick (post-refresh), but it must be cheap:

- Build a list of selected sensors.
- For each selected sensor:
  - If sensor has `Value == null`, skip
  - If `Value` is unchanged from last publish (optional optimization), skip
  - Publish payload according to topic and payload formats below

The publish path must be non-blocking:
- If not connected: drop quickly (no large queue).
- If publishing throws: catch and log, do not crash app.
- Consider a small bounded queue (optional). Default behavior can be “drop when busy”.


## Topic scheme

### Base
- `baseTopic` from settings (default `librehardwaremonitor`)

### Full topic (per sensor)
Recommended:
- `{baseTopic}/{machine}/{hardwareId}/{sensorId}`

Where:
- `machine` = `Environment.MachineName` (sanitized)
- `hardwareId` = derived from existing hardware identifier (sanitized)
- `sensorId` = derived from sensor identifier (sanitized)

Sanitization:
- replace whitespace with `_`
- replace `/` with `_` (or keep `/` only for topic separators, not inside segments)
- strip characters not suitable for MQTT topics if needed

### Optional convenience topics (nice-to-have)
- `{topic}/value` publishing just the numeric value as a string
- `{baseTopic}/{machine}/status` retained online/offline indicator (LWT)


## Payload format

### Default payload: JSON
Publish a JSON document per sensor (example):

    {
      "name": "CPU Core #1",
      "value": 42.5,
      "unit": "°C",
      "sensorType": "Temperature",
      "hardwareName": "Intel Core ...",
      "hardwareType": "Cpu",
      "identifier": "/intelcpu/0/temperature/1",
      "timestamp": "2026-02-24T12:34:56.789Z"
    }

Notes:
- `identifier` should use the existing LHM `sensor.Identifier.ToString()` (or equivalent stable representation).
- `timestamp` should be UTC ISO-8601.

### Units
Use the same formatting logic the UI uses where feasible:
- If there’s an existing `SensorNode.Format` used by HttpServer JSON generation, prefer that pattern.
- Otherwise, include the raw value and an inferred unit string.


## Integration points in code

### MainForm additions
- add a `public MqttBroadcaster Mqtt { get; }` or private field
- create `UserOption _runMqttBroadcaster` bound to the Run menu item
  - `.Changed` handler starts/stops `MqttBroadcaster` (like `_runWebServer`)
- add menu click handler for Settings… dialog

### Update pipeline hook
After sensors refresh completes (post `_computer.Accept(_updateVisitor)`), call something like:

- `Mqtt.PublishSelectedSensors(_root, DateTime.UtcNow)`
  - where `_root` is the same Node tree HttpServer traverses
  - OR publish from the Computer hardware list directly
  - preferred: publish from the same stable sensor identifiers used for context menus

### Sensor selection key usage
When building sensor context menu:
- determine checked state:
  - `settings.GetValue(new Identifier(sensor.Identifier, "mqtt").ToString(), false)`
- on toggle:
  - set/remove key accordingly


## Logging & diagnostics
- Use existing logging approach used by HttpServer (or project’s standard).
- Log:
  - start/stop
  - connection success/failure
  - reconnect attempts (throttled)
  - publish exceptions (throttled)


## Acceptance criteria checklist

### Functional
- MQTT toggles on/off via Options menu and persists across restarts.
- Settings dialog edits host/port/etc and persists.
- “Broadcast via MQTT” per-sensor checkbox persists and controls publishing.
- Publishing cadence follows Update Interval (no separate timer).
- App remains responsive if broker is offline.

### Stability
- No crashes if broker disappears mid-run.
- Reconnect occurs automatically with backoff.
- Clean shutdown (Stop/Dispose called on exit).

### Maintainability
- MQTT code mostly contained within `MqttBroadcaster.cs`.
- Integration touches only:
  - MainForm (menu + lifecycle + update hook)
  - Settings + small dialog form
  - sensor context menu builder