using DevelApp.PublicApiSurface;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DevelApp.PublicApiSurface <assembly.dll> [<assembly2.dll> ...]");
    Console.Error.WriteLine("Prints the canonical public-API surface, one member per line, sorted.");
    return 2;
}

foreach (var path in args)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Assembly not found: {path}");
        return 1;
    }
}

var surface = new List<string>();
foreach (var path in args)
{
    using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
    surface.AddRange(ApiSurface.Extract(assembly));
}

surface.Sort(StringComparer.Ordinal);
foreach (var line in surface)
{
    Console.WriteLine(line);
}

return 0;
