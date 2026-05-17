# Auxilia.SteeringInstance.Tests

Unit tests for `Auxilia.SteeringInstance`.

## File / Folder Map
```
Auxilia.SteeringInstance.Tests/
├── EnvironmentValidatorTests.cs               # Tool/OS/Port satisfied vs unsatisfied; unknown requirement
├── ConfigurationResolverTests.cs              # No configs, dirty configs, bad key, happy-path encryption
├── DirtyConfigurationDetectorTests.cs         # First schema (no diff), added field → dirty, no change → clean
├── SlotConfigurationStoreTests.cs             # Upsert add/update, MarkDirty, GetConfigurations
├── WorkflowSchemaStoreTests.cs                # TryGetSchema miss/hit, SetSchema
└── Workflows/WorkflowRegistrationHandlerTests.cs  # Full handler flow with faked message bus
```