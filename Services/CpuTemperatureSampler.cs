using System.Reflection;

namespace ANEVRED.Services;

internal sealed class CpuTemperatureSampler
{
    private DateTime _lastRead = DateTime.MinValue;
    private double _lastTemperature;

    public double Read()
    {
        if ((DateTime.UtcNow - _lastRead) < TimeSpan.FromSeconds(5))
        {
            return _lastTemperature;
        }

        _lastRead = DateTime.UtcNow;
        _lastTemperature = TryReadAcpiTemperature();
        return _lastTemperature;
    }

    private static double TryReadAcpiTemperature()
    {
        try
        {
            var assembly = Assembly.Load("System.Management");
            var searcherType = assembly.GetType("System.Management.ManagementObjectSearcher");
            if (searcherType is null)
            {
                return 0;
            }

            using var searcher = Activator.CreateInstance(
                searcherType,
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature") as IDisposable;

            if (searcher is null)
            {
                return 0;
            }

            var getMethod = searcherType.GetMethod("Get", Type.EmptyTypes);
            var results = getMethod?.Invoke(searcher, null) as System.Collections.IEnumerable;
            if (results is null)
            {
                return 0;
            }

            var values = new List<double>();
            foreach (var item in results)
            {
                var method = item.GetType().GetMethod("GetPropertyValue", [typeof(string)]);
                var raw = method?.Invoke(item, ["CurrentTemperature"]);
                if (raw is null)
                {
                    continue;
                }

                var celsius = Convert.ToDouble(raw) / 10d - 273.15d;
                if (!double.IsNaN(celsius) && !double.IsInfinity(celsius) && celsius is > 0 and < 130)
                {
                    values.Add(celsius);
                }
            }

            return values.Count == 0 ? 0 : values.Max();
        }
        catch
        {
            return 0;
        }
    }
}
