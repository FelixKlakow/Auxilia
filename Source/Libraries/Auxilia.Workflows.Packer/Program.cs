using Auxilia.Workflows.Packer;

var input = string.Empty;
var output = string.Empty;
var key = string.Empty;

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--input":
            input = args[++i];
            break;
        case "--output":
            output = args[++i];
            break;
        case "--key":
            key = args[++i];
            break;
    }
}

if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(key))
{
    await Console.Error.WriteLineAsync("Usage: auxilia-packer --input <dir> --output <file> --key <pem-path>");
    return 1;
}

try
{
    using var signer = new RsaFileSigningProvider(key);
    var packer = new WorkflowPacker(signer);
    packer.Pack(input, output);
    Console.WriteLine($"Workflow package created: {output}");
    return 0;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Error: {ex.Message}");
    return 1;
}
