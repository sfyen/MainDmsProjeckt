using System.Runtime.Loader;

namespace DmsProjeckt.Helpers
{
    public sealed class CustomAssemblyLoadContext : AssemblyLoadContext
    {
        public IntPtr LoadUnmanagedLibrary(string absolutePath)
        {
            // Lädt EXAKT den Pfad (kein ".dll" anhängen!)
            return LoadUnmanagedDllFromPath(absolutePath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // Wenn irgendwo nur ein Name übergeben wird, nicht erraten/anhängen:
            return IntPtr.Zero;
        }

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
        {
            return null;
        }
    }
}
