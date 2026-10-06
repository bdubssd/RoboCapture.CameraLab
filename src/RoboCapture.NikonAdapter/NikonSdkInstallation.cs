namespace RoboCapture.NikonAdapter;

public static class NikonSdkInstallation
{
    private static readonly string[] ProfileNames =
        ["DC_PTP_Config.config", "MaidLayer.config", "RangeValue.config"];

    public static string ResolveModuleDirectory(string path, string? baseDirectory = null)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        for (var directory = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.FullName, path));
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.GetFullPath(path);
    }

    public static void EnsureProfiles(string moduleDirectory, string? destinationDirectory = null)
    {
        var destination = destinationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nikon", "NXTether");
        Directory.CreateDirectory(destination);
        foreach (var name in ProfileNames)
        {
            var target = Path.Combine(destination, name);
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
            var source = Path.Combine(moduleDirectory, name);
            using var input = File.Exists(source) && new FileInfo(source).Length > 0
                ? File.OpenRead(source)
                : typeof(NikonSdkInstallation).Assembly.GetManifestResourceStream("NikonProfiles." + name);
            if (input is null)
                throw new FileNotFoundException(
                    $"Nikon configuration '{name}' is missing. Restore it in the SDK folder '{moduleDirectory}' and reconnect.", source);
            // Rename a completed copy so interruption cannot leave a partial profile.
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
