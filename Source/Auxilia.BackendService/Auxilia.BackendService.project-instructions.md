# Auxilia.BackendService

Discoverable worker service. Responds to `IdentificationRequestMessage` with service identity (ID, version, purpose, startup time). Acts as the foundation for any concrete backend service — other services extend this pattern.

## Architecture

Two hosted services run on startup: `QueueInitializer` declares the queue, then `IdentificationRequestHandler` subscribes to it. The queue name defaults to `"backend-service"` but is overridable via config, allowing multiple instances to coexist.

`ServiceInfo` is a singleton captured before the host is built so the instance GUID matches the log-file name.

## File / Folder Map
```
Source/Auxilia.BackendService/
├── Program.cs                          # Host wiring; generates instanceId before builder so log file name matches ServiceInfo
├── ServiceInfo.cs                      # Singleton identity: ServiceId, Version, StartupTimeUtc
├── QueueInitializer.cs                 # IHostedService: declares the queue on startup
├── IdentificationRequestHandler.cs     # IHostedService: subscribes, responds, records telemetry
└── BackendServiceTelemetry.cs          # ActivitySource + metrics instruments
```