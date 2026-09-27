using System.Runtime.CompilerServices;
using System.Text;

namespace Avalanche.Services;

internal static class RuntimeEncodingBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
