using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class PluginLoaderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateTempDllFile()
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.slothandler.dll");
        File.WriteAllBytes(path, [0x42]);
        return path;
    }

    private static Assembly BuildSingleHandlerAssembly()
    {
        var assemblyName = new AssemblyName($"TestPlugin_{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("TestPlugin");

        var typeBuilder = module.DefineType(
            "TestSlotHandler",
            TypeAttributes.Public | TypeAttributes.Class);
        typeBuilder.AddInterfaceImplementation(typeof(ISlotHandler));

        var registerMethod = typeBuilder.DefineMethod(
            "Register",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            returnType: null,
            parameterTypes: [typeof(IServiceCollection), typeof(string), typeof(SlotConfiguration)]);
        registerMethod.GetILGenerator().Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(registerMethod, typeof(ISlotHandler).GetMethod("Register")!);

        typeBuilder.CreateType();
        return assembly;
    }

    private static Assembly BuildNoHandlerAssembly()
    {
        var assemblyName = new AssemblyName($"EmptyPlugin_{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("EmptyPlugin");
        return assembly;
    }

    private static DiscoveredPlugin MakePlugin(string path, string providerType = "test-provider")
        => new(providerType, path, new PluginManifest(providerType, "aA==", "cw==", "cA=="));

    [Test]
    public void Load_ValidAssemblyVerifierTrue_RegistersHandler()
    {
        var dllPath = CreateTempDllFile();
        var inMemoryAssembly = BuildSingleHandlerAssembly();

        var verifier = new Mock<IPluginManifestVerifier>();
        verifier.Setup(v => v.Verify(It.IsAny<PluginManifest>(), It.IsAny<byte[]>())).Returns(true);

        var resolver = new SlotHandlerResolver();

        var loader = new PluginLoader(resolver, verifier.Object, _ => inMemoryAssembly);
        loader.Load([MakePlugin(dllPath, "my-provider")]);

        var handler = resolver.Resolve("my-provider");
        Assert.That(handler, Is.Not.Null);
    }

    [Test]
    public void Load_VerifierReturnsFalse_ThrowsPluginVerificationException()
    {
        var dllPath = CreateTempDllFile();
        var assemblyLoaderCalled = false;

        var verifier = new Mock<IPluginManifestVerifier>();
        verifier.Setup(v => v.Verify(It.IsAny<PluginManifest>(), It.IsAny<byte[]>())).Returns(false);

        var resolver = new SlotHandlerResolver();

        var loader = new PluginLoader(resolver, verifier.Object, _ =>
        {
            assemblyLoaderCalled = true;
            return BuildSingleHandlerAssembly();
        });

        var ex = Assert.Throws<PluginVerificationException>(() =>
            loader.Load([MakePlugin(dllPath, "bad-provider")]));

        Assert.That(ex!.ProviderType, Is.EqualTo("bad-provider"));
        Assert.That(assemblyLoaderCalled, Is.False);
    }

    [Test]
    public void Load_AssemblyWithNoHandlerType_ThrowsInvalidOperationException()
    {
        var dllPath = CreateTempDllFile();
        var emptyAssembly = BuildNoHandlerAssembly();

        var verifier = new Mock<IPluginManifestVerifier>();
        verifier.Setup(v => v.Verify(It.IsAny<PluginManifest>(), It.IsAny<byte[]>())).Returns(true);

        var resolver = new SlotHandlerResolver();
        var loader = new PluginLoader(resolver, verifier.Object, _ => emptyAssembly);

        Assert.Throws<InvalidOperationException>(() =>
            loader.Load([MakePlugin(dllPath)]));
    }

    [Test]
    public void Load_TwoValidPlugins_BothRegistered()
    {
        var dllA = CreateTempDllFile();
        var dllB = CreateTempDllFile();
        var assemblyA = BuildSingleHandlerAssembly();
        var assemblyB = BuildSingleHandlerAssembly();

        var assemblies = new Dictionary<string, Assembly>
        {
            [dllA] = assemblyA,
            [dllB] = assemblyB
        };

        var verifier = new Mock<IPluginManifestVerifier>();
        verifier.Setup(v => v.Verify(It.IsAny<PluginManifest>(), It.IsAny<byte[]>())).Returns(true);

        var resolver = new SlotHandlerResolver();
        var loader = new PluginLoader(resolver, verifier.Object, path => assemblies[path]);

        loader.Load([MakePlugin(dllA, "provider-a"), MakePlugin(dllB, "provider-b")]);

        var handlerA = resolver.Resolve("provider-a");
        var handlerB = resolver.Resolve("provider-b");
        Assert.That(handlerA, Is.Not.Null);
        Assert.That(handlerB, Is.Not.Null);
        Assert.That(handlerA, Is.Not.SameAs(handlerB));
    }
}
