using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DmsProjeckt.Helpers
{
    public sealed class CustomAssemblyLoadContext : AssemblyLoadContext
    {
        public IntPtr LoadUnmanagedLibrary(string absolutePath)
        {
            return LoadUnmanagedDllFromPath(absolutePath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            return IntPtr.Zero;
        }

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
        {
            return null;
        }

        /// <summary>

        /// </summary>
        public static string GetLibraryPath()
        {
            string nativeFolder = Path.Combine(AppContext.BaseDirectory, "Native");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Path.Combine(nativeFolder, "libwkhtmltox.dll");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Path.Combine(nativeFolder, "libwkhtmltox.dylib");
            else
                return Path.Combine(nativeFolder, "libwkhtmltox.so");
        }
    }
}
