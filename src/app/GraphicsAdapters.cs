using Vortice.DXGI;

namespace QFTPlus;

internal static class GraphicsAdapters
{
    internal static List<(int Index, string Name)> List()
    {
        var result = new List<(int, string)>();
        if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure) return result;
        using (factory)
            for (uint index = 0; index < 16 && factory!.EnumAdapters1(index, out var adapter).Success; index++)
                using (adapter)
                    if (adapter.Description1 is var description && (description.Flags & AdapterFlags.Software) == 0) result.Add(((int)index, description.Description));
        return result;
    }
}
