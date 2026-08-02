# Auxilia.Workflows.SlotPackages.Tests

Unit tests for the three slot-package libraries: `SourceControl`, `TaskSource`, and `AiAgent`. Covers capability record serialisation, enum values, and builder extension methods.

## File / Folder Map
```
Tests/Libraries/Auxilia.Workflows.SlotPackages.Tests/
├── SourceControl/
│   ├── SourceControlCapabilitiesTests.cs   # JSON round-trip, required fields, extension data preserved
│   └── RequiresSourceControlTests.cs       # Extension method produces correct SlotDefinition
├── TaskSource/
│   ├── TaskSourceCapabilitiesTests.cs      # JSON round-trip, required fields
│   └── RequiresTaskSourceTests.cs          # Extension method produces correct SlotDefinition
└── AiAgent/
    ├── AiCapabilitiesTests.cs              # JSON round-trip, required + optional fields
    ├── RequiresAiAgentTests.cs             # Extension method produces correct SlotDefinition
    └── ModalityTests.cs                    # Enum values; serialises as string
```