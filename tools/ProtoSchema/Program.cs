using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ProtoBuf;
using ProtoBuf.Meta;

namespace ProtoSchema
{
    // Writes the .proto schema of every [ProtoContract] type in the game's
    // JunkWars.dll, using the game's own protobuf-net (2.0.0.668) so the field
    // numbers, inheritance and defaults are exactly what the client expects.
    //
    //   dotnet run -- <GAR_Data\Managed folder> <output .proto>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string managed = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "GAR_Data", "Managed"));
            string output = args.Length > 1 ? args[1] : "junkwars.proto";
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            return Run(managed, output);
        }

        // Separate so protobuf-net is only needed once the resolver above is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string managed, string output)
        {
            Assembly junkWars = Assembly.LoadFrom(Path.Combine(managed, "JunkWars.dll"));
            Type[] contracts = junkWars.GetTypes()
                .Where(t => !t.IsGenericTypeDefinition && t.GetCustomAttributes(typeof(ProtoContractAttribute), false).Length > 0)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToArray();

            RuntimeTypeModel model = RuntimeTypeModel.Create();
            int added = 0;
            foreach (Type type in contracts)
            {
                try
                {
                    model.Add(type, true);
                    added++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("skipped " + type.FullName + ": " + ex.Message);
                }
            }

            string schema = model.GetSchema(null);
            string header =
                "// Guns and Robots data contracts, generated from the game's JunkWars.dll\n" +
                "// with its own protobuf-net 2.0.0.668 by GARRevival/tools/ProtoSchema.\n" +
                "// " + added + " contract types. Every byte[] parameter in PROTOCOL.md that is\n" +
                "// marked proto/lz4/bzip2 holds one of these messages (see PROTOCOL.md).\n\n";
            File.WriteAllText(output, header + schema);
            Console.WriteLine("Wrote " + output + " (" + added + " of " + contracts.Length + " contract types)");
            return 0;
        }
    }
}
