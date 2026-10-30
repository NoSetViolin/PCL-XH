using System.IO;
using System.Reflection;
using PCL.Core.Updates;

// A disposable executable for testing Windows image locks, restart and rollback.
var generation = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(a => a.Key == "FixtureGeneration").Value!;
var root = AppContext.BaseDirectory;
var context = AtomicLauncherUpdate.ConsumeStartupContext();
if (args.Contains("--wait"))
{
    File.WriteAllText(Path.Combine(root, "parent.ready"), Environment.ProcessId.ToString());
    while (!File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(100);
    return;
}
File.WriteAllText(Path.Combine(root, "started-" + generation), Environment.ProcessId.ToString());
if (generation == "new" && File.Exists(Path.Combine(root, "crash-new"))) Environment.Exit(42);
if (generation != "new" || !File.Exists(Path.Combine(root, "hang-new")))
    AtomicLauncherUpdate.AcknowledgeStartup(context, Environment.ProcessPath!);
while (!File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(100);
