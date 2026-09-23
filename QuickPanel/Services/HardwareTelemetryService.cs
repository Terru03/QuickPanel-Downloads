using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace QuickPanel.Services;

public sealed record HardwarePerformanceItem(
	string Category,
	string Name,
	string Usage,
	string Temperature,
	string Fan,
	string Capacity,
	string Details);

public sealed record HardwareTelemetrySample(
	double? CpuPercent,
	double? MemoryPercent,
	double? GpuPercent,
	double? StoragePercent,
	double? TemperatureCelsius,
	double? FanRpm);

public sealed record HardwarePerformanceSnapshot(
	string CpuSummary,
	string MemorySummary,
	string GpuSummary,
	string StorageSummary,
	string TemperatureSummary,
	string FanSummary,
	HardwareTelemetrySample Sample,
	IReadOnlyList<HardwarePerformanceItem> Items,
	string SensorAccessNote);

public sealed class HardwareTelemetryService
{
	private sealed record SensorReading(string Name, string Type, double Value);

	private sealed record HardwareNode(string Name, string Type, IReadOnlyList<SensorReading> Sensors);

	private sealed record PhysicalDiskInfo(string Name, string MediaType, string BusType, ulong SizeBytes, string Health);

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatus
	{
		internal uint Length;
		internal uint MemoryLoad;
		internal ulong TotalPhysical;
		internal ulong AvailablePhysical;
		internal ulong TotalPageFile;
		internal ulong AvailablePageFile;
		internal ulong TotalVirtual;
		internal ulong AvailableVirtual;
		internal ulong AvailableExtendedVirtual;
	}

	private Computer? _computer;
	private DateTime _nextSensorOpenAttemptUtc;
	private DateTime _inventoryCapturedAtUtc;
	private IReadOnlyList<PhysicalDiskInfo> _physicalDisks = [];
	private IReadOnlyList<string> _fallbackGpuNames = [];

	public HardwarePerformanceSnapshot Capture()
	{
		List<HardwareNode> nodes = CaptureSensorNodes(out string sensorError);
		RefreshInventoryIfNeeded();
		List<HardwarePerformanceItem> items = [];

		List<HardwareNode> cpuNodes = nodes.Where(node => node.Type.Equals("Cpu", StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (HardwareNode cpu in cpuNodes)
		{
			items.Add(CreateCpuItem(cpu));
		}

		MemoryStatus memory = CaptureMemory();
		HardwareNode? memoryNode = nodes.FirstOrDefault(node => node.Type.Equals("Memory", StringComparison.OrdinalIgnoreCase));
		items.Add(CreateMemoryItem(memory, memoryNode));

		List<HardwareNode> gpuNodes = nodes.Where(node => node.Type.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (HardwareNode gpu in gpuNodes)
		{
			items.Add(CreateGpuItem(gpu));
		}
		foreach (string gpuName in _fallbackGpuNames.Where(name => gpuNodes.All(node => !NamesOverlap(node.Name, name))))
		{
			items.Add(new HardwarePerformanceItem("GPU", gpuName, "Unavailable", "Unavailable", "Unavailable", "Unavailable", "Live sensors were not exposed."));
		}

		List<HardwareNode> storageNodes = nodes.Where(node => node.Type.Equals("Storage", StringComparison.OrdinalIgnoreCase)).ToList();
		HashSet<PhysicalDiskInfo> matchedDisks = [];
		foreach (HardwareNode storage in storageNodes)
		{
			PhysicalDiskInfo? disk = _physicalDisks.FirstOrDefault(candidate => !matchedDisks.Contains(candidate) && NamesOverlap(storage.Name, candidate.Name));
			if (disk != null) matchedDisks.Add(disk);
			items.Add(CreateStorageItem(storage, disk));
		}
		foreach (PhysicalDiskInfo disk in _physicalDisks.Where(disk => !matchedDisks.Contains(disk)))
		{
			items.Add(CreateStorageItem(null, disk));
		}

		AddCoolingAndBoardItems(nodes, items);
		AddOtherHardwareItems(nodes, items);

		HardwarePerformanceItem? firstCpu = items.FirstOrDefault(item => item.Category.Equals("CPU", StringComparison.Ordinal));
		HardwarePerformanceItem memoryItem = items.First(item => item.Category.Equals("Memory", StringComparison.Ordinal));
		List<HardwarePerformanceItem> gpuItems = items.Where(item => item.Category.Equals("GPU", StringComparison.Ordinal)).ToList();
		List<HardwarePerformanceItem> diskItems = items.Where(item => item.Category is "NVMe" or "SSD" or "HDD" or "Disk").ToList();
		double[] temperatureReadings = nodes
			.SelectMany(node => node.Sensors)
			.Where(sensor => sensor.Type.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
			.Select(sensor => sensor.Value)
			.ToArray();
		double[] fanReadings = nodes
			.SelectMany(node => node.Sensors)
			.Where(sensor => sensor.Type.Equals("Fan", StringComparison.OrdinalIgnoreCase))
			.Select(sensor => sensor.Value)
			.ToArray();
		double? cpuPercent = MaxAvailable(cpuNodes.Select(node => FindPreferredSensor(node.Sensors, "Load", ["CPU Total", "Total"])));
		double? memoryPercent = memory.TotalPhysical > 0
			? (memory.TotalPhysical - Math.Min(memory.TotalPhysical, memory.AvailablePhysical)) * 100.0 / memory.TotalPhysical
			: FindPreferredSensor(memoryNode?.Sensors ?? [], "Load", ["Memory"]);
		double? gpuPercent = MaxAvailable(gpuNodes.Select(node => FindPreferredSensor(node.Sensors, "Load", ["GPU Core", "D3D 3D", "Core"])));
		double? storagePercent = MaxAvailable(storageNodes.Select(node => FindPreferredSensor(node.Sensors, "Load", ["Total Activity", "Used Space", "Activity"])));
		double? hottestTemperature = MaxAvailable(temperatureReadings.Where(value => value > 1.0).Select(value => (double?)value));
		double? fastestFan = MaxAvailable(fanReadings.Where(value => value > 0.0).Select(value => (double?)value));
		bool hasFanSensor = fanReadings.Any(value => value > 0.0);
		string sensorNote = !string.IsNullOrWhiteSpace(sensorError)
			? sensorError
			: hasFanSensor
				? "Live sensors are available. Hardware that does not publish a sensor is marked Unavailable."
				: "No fan controller was exposed. Fan RPM may require administrator access, motherboard vendor software, or unsupported firmware.";
		return new HardwarePerformanceSnapshot(
			firstCpu == null ? "CPU unavailable" : firstCpu.Usage + " · " + firstCpu.Temperature,
			memoryItem.Usage,
			gpuItems.Count == 0 ? "GPU unavailable" : gpuItems.Count == 1 ? gpuItems[0].Usage + " · " + gpuItems[0].Temperature : gpuItems.Count + " GPUs detected",
			diskItems.Count == 0 ? "Storage unavailable" : diskItems.Count + " drive" + (diskItems.Count == 1 ? string.Empty : "s"),
			TaskManagerPresentationPolicy.FormatTemperatureSummary(temperatureReadings),
			TaskManagerPresentationPolicy.FormatFanSummary(fanReadings),
			new HardwareTelemetrySample(cpuPercent, memoryPercent, gpuPercent, storagePercent, hottestTemperature, fastestFan),
			items,
			sensorNote);
	}

	public void Close()
	{
		try
		{
			_computer?.Close();
		}
		catch (Exception exception) when (IsExpectedHardwareException(exception))
		{
			// A hardware driver can disappear during shutdown or sleep.
		}
		_computer = null;
	}

	private List<HardwareNode> CaptureSensorNodes(out string sensorError)
	{
		sensorError = string.Empty;
		if (_computer == null && DateTime.UtcNow >= _nextSensorOpenAttemptUtc)
		{
			try
			{
				_computer = new Computer
				{
					IsCpuEnabled = true,
					IsGpuEnabled = true,
					IsMemoryEnabled = true,
					IsMotherboardEnabled = true,
					IsControllerEnabled = true,
					IsStorageEnabled = true,
					IsNetworkEnabled = true,
					IsPsuEnabled = true,
					IsBatteryEnabled = true
				};
				_computer.Open();
			}
			catch (Exception exception) when (IsExpectedHardwareException(exception))
			{
				Close();
				_nextSensorOpenAttemptUtc = DateTime.UtcNow.AddSeconds(30);
				sensorError = "Live hardware sensors are unavailable: " + exception.Message;
			}
		}
		if (_computer == null) return [];

		List<HardwareNode> nodes = [];
		foreach (IHardware hardware in _computer.Hardware)
		{
			CaptureHardwareRecursive(hardware, nodes);
		}
		return nodes;
	}

	private static void CaptureHardwareRecursive(IHardware hardware, List<HardwareNode> nodes)
	{
		try
		{
			hardware.Update();
			List<SensorReading> sensors = hardware.Sensors
				.Where(sensor => sensor.Value.HasValue && double.IsFinite(sensor.Value.Value))
				.Select(sensor => new SensorReading(sensor.Name, sensor.SensorType.ToString(), sensor.Value!.Value))
				.ToList();
			nodes.Add(new HardwareNode(hardware.Name, hardware.HardwareType.ToString(), sensors));
		}
		catch (Exception exception) when (IsExpectedHardwareException(exception))
		{
			// Keep the other devices when one controller cannot be queried.
		}
		foreach (IHardware subHardware in hardware.SubHardware)
		{
			CaptureHardwareRecursive(subHardware, nodes);
		}
	}

	private void RefreshInventoryIfNeeded()
	{
		if (DateTime.UtcNow - _inventoryCapturedAtUtc < TimeSpan.FromSeconds(30)) return;
		_inventoryCapturedAtUtc = DateTime.UtcNow;
		_physicalDisks = ReadPhysicalDisks();
		_fallbackGpuNames = ReadGpuNames();
	}

	private static IReadOnlyList<PhysicalDiskInfo> ReadPhysicalDisks()
	{
		List<PhysicalDiskInfo> disks = [];
		try
		{
			ManagementScope scope = new(@"\\.\ROOT\Microsoft\Windows\Storage");
			scope.Connect();
			using ManagementObjectSearcher searcher = new(scope, new ObjectQuery("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));
			using ManagementObjectCollection results = searcher.Get();
			foreach (ManagementObject disk in results.Cast<ManagementObject>())
			{
				using (disk)
				{
					ushort mediaType = ConvertToUInt16(disk["MediaType"]);
					ushort busType = ConvertToUInt16(disk["BusType"]);
					ushort healthStatus = ConvertToUInt16(disk["HealthStatus"]);
					disks.Add(new PhysicalDiskInfo(
						Convert.ToString(disk["FriendlyName"], CultureInfo.CurrentCulture) ?? "Physical disk",
						mediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Disk" },
						busType switch { 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Virtual", 16 => "Storage Spaces", 17 => "NVMe", _ => "Unknown bus" },
						ConvertToUInt64(disk["Size"]),
						healthStatus switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown health" }));
				}
			}
		}
		catch (Exception exception) when (IsExpectedHardwareException(exception))
		{
			// WMI inventory is optional; live storage sensors can still be shown.
		}
		return disks;
	}

	private static IReadOnlyList<string> ReadGpuNames()
	{
		List<string> names = [];
		try
		{
			using ManagementObjectSearcher searcher = new("SELECT Name FROM Win32_VideoController");
			using ManagementObjectCollection results = searcher.Get();
			foreach (ManagementObject gpu in results.Cast<ManagementObject>())
			{
				using (gpu)
				{
					string name = Convert.ToString(gpu["Name"], CultureInfo.CurrentCulture) ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
				}
			}
		}
		catch (Exception exception) when (IsExpectedHardwareException(exception))
		{
			// Sensor enumeration may still provide GPU data.
		}
		return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static HardwarePerformanceItem CreateCpuItem(HardwareNode cpu)
	{
		double? usage = FindPreferredSensor(cpu.Sensors, "Load", ["CPU Total", "Total"]);
		double? temperature = FindMaxSensor(cpu.Sensors, "Temperature");
		double? fan = FindMaxSensor(cpu.Sensors, "Fan");
		double? clock = FindMaxSensor(cpu.Sensors, "Clock");
		double? power = FindMaxSensor(cpu.Sensors, "Power");
		string details = JoinDetails(
			clock.HasValue ? FormatNumber(clock.Value / 1000.0, "0.00") + " GHz max clock" : null,
			power.HasValue ? FormatNumber(power.Value, "0.0") + " W" : null);
		return new HardwarePerformanceItem(
			"CPU",
			cpu.Name,
			FormatPercent(usage),
			FormatTemperature(temperature),
			FormatFan(fan),
			GetLogicalProcessorCount().ToString(CultureInfo.CurrentCulture) + " logical processors",
			details);
	}

	private static HardwarePerformanceItem CreateMemoryItem(MemoryStatus memory, HardwareNode? memoryNode)
	{
		ulong used = memory.TotalPhysical > memory.AvailablePhysical ? memory.TotalPhysical - memory.AvailablePhysical : 0;
		double? usage = memory.TotalPhysical > 0 ? used * 100.0 / memory.TotalPhysical : FindPreferredSensor(memoryNode?.Sensors ?? [], "Load", ["Memory"]);
		string usageText = memory.TotalPhysical > 0
			? FormatBytes(used) + " / " + FormatBytes(memory.TotalPhysical) + " (" + FormatNumber(usage ?? 0, "0.0") + "%)"
			: FormatPercent(usage);
		return new HardwarePerformanceItem(
			"Memory",
			"Physical RAM",
			usageText,
			"—",
			"—",
			memory.TotalPhysical > 0 ? FormatBytes(memory.TotalPhysical) : "Unavailable",
			memory.AvailablePhysical > 0 ? FormatBytes(memory.AvailablePhysical) + " available" : "Windows did not expose capacity.");
	}

	private static HardwarePerformanceItem CreateGpuItem(HardwareNode gpu)
	{
		double? usage = FindPreferredSensor(gpu.Sensors, "Load", ["GPU Core", "D3D 3D", "Core"]);
		double? temperature = FindMaxSensor(gpu.Sensors, "Temperature");
		double? fan = FindMaxSensor(gpu.Sensors, "Fan");
		double? memoryUsed = FindPreferredSensor(gpu.Sensors, "SmallData", ["Memory Used", "GPU Memory Used"])
			?? FindPreferredSensor(gpu.Sensors, "Data", ["Memory Used", "GPU Memory Used"]);
		double? memoryTotal = FindPreferredSensor(gpu.Sensors, "SmallData", ["Memory Total", "GPU Memory Total"])
			?? FindPreferredSensor(gpu.Sensors, "Data", ["Memory Total", "GPU Memory Total"]);
		double? power = FindMaxSensor(gpu.Sensors, "Power");
		string capacity = memoryTotal.HasValue
			? FormatNumber(memoryTotal.Value, "N0") + " MB VRAM"
			: "Unavailable";
		string details = JoinDetails(
			memoryUsed.HasValue ? FormatNumber(memoryUsed.Value, "N0") + " MB VRAM used" : null,
			power.HasValue ? FormatNumber(power.Value, "0.0") + " W" : null);
		return new HardwarePerformanceItem("GPU", gpu.Name, FormatPercent(usage), FormatTemperature(temperature), FormatFan(fan), capacity, details);
	}

	private static HardwarePerformanceItem CreateStorageItem(HardwareNode? storage, PhysicalDiskInfo? disk)
	{
		double? usage = FindPreferredSensor(storage?.Sensors ?? [], "Load", ["Total Activity", "Used Space", "Activity"]);
		double? temperature = FindMaxSensor(storage?.Sensors ?? [], "Temperature");
		string category = disk?.BusType == "NVMe" ? "NVMe" : disk?.MediaType ?? "Disk";
		string name = storage?.Name ?? disk?.Name ?? "Physical disk";
		string details = disk == null ? "Physical disk type was not exposed by Windows." : disk.BusType + " · " + disk.Health;
		return new HardwarePerformanceItem(
			category,
			name,
			FormatPercent(usage),
			FormatTemperature(temperature),
			"—",
			disk is { SizeBytes: > 0 } ? FormatBytes(disk.SizeBytes) : "Unavailable",
			details);
	}

	private static void AddCoolingAndBoardItems(IEnumerable<HardwareNode> nodes, ICollection<HardwarePerformanceItem> items)
	{
		foreach (HardwareNode node in nodes.Where(node => node.Type is "Motherboard" or "SuperIO" or "EmbeddedController"))
		{
			List<SensorReading> fans = node.Sensors.Where(sensor => sensor.Type.Equals("Fan", StringComparison.OrdinalIgnoreCase)).ToList();
			List<SensorReading> temperatures = node.Sensors.Where(sensor => sensor.Type.Equals("Temperature", StringComparison.OrdinalIgnoreCase)).ToList();
			if (fans.Count == 0 && temperatures.Count == 0) continue;
			string details = string.Join(" · ", fans.Take(4).Select(sensor => sensor.Name + " " + FormatNumber(sensor.Value, "N0") + " RPM"));
			items.Add(new HardwarePerformanceItem(
				fans.Count > 0 ? "Cooling" : "Board",
				node.Name,
				"—",
				FormatTemperature(temperatures.Count == 0 ? null : temperatures.Max(sensor => sensor.Value)),
				FormatFan(fans.Count == 0 ? null : fans.Max(sensor => sensor.Value)),
				"—",
				string.IsNullOrWhiteSpace(details) ? "Motherboard telemetry" : details));
		}
	}

	private static void AddOtherHardwareItems(IEnumerable<HardwareNode> nodes, ICollection<HardwarePerformanceItem> items)
	{
		HashSet<string> handledTypes = new(StringComparer.OrdinalIgnoreCase) { "Cpu", "Memory", "Storage", "Motherboard", "SuperIO", "EmbeddedController" };
		foreach (HardwareNode node in nodes.Where(node => !handledTypes.Contains(node.Type) && !node.Type.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)))
		{
			if (node.Sensors.Count == 0) continue;
			double? load = FindMaxSensor(node.Sensors, "Load");
			double? temperature = FindMaxSensor(node.Sensors, "Temperature");
			double? fan = FindMaxSensor(node.Sensors, "Fan");
			double? power = FindMaxSensor(node.Sensors, "Power");
			items.Add(new HardwarePerformanceItem(
				node.Type,
				node.Name,
				FormatPercent(load),
				FormatTemperature(temperature),
				FormatFan(fan),
				"—",
				power.HasValue ? FormatNumber(power.Value, "0.0") + " W" : node.Sensors.Count + " live sensors"));
		}
	}

	private static MemoryStatus CaptureMemory()
	{
		MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
		return GlobalMemoryStatusEx(ref memory) ? memory : new MemoryStatus();
	}

	private static double? FindPreferredSensor(IReadOnlyList<SensorReading> sensors, string type, IReadOnlyList<string> preferredNames)
	{
		List<SensorReading> matching = sensors.Where(sensor => sensor.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (string preferredName in preferredNames)
		{
			SensorReading? preferred = matching.FirstOrDefault(sensor => sensor.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
			if (preferred != null) return preferred.Value;
		}
		return matching.Count == 0 ? null : matching.Max(sensor => sensor.Value);
	}

	private static double? FindMaxSensor(IReadOnlyList<SensorReading> sensors, string type)
	{
		List<double> values = sensors
			.Where(sensor => sensor.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
			.Select(sensor => sensor.Value)
			.Where(value => IsMeaningfulSensorValue(type, value))
			.ToList();
		return values.Count == 0 ? null : values.Max();
	}

	private static double? MaxAvailable(IEnumerable<double?> values)
	{
		double[] available = values.Where(value => value.HasValue && double.IsFinite(value.Value)).Select(value => value!.Value).ToArray();
		return available.Length == 0 ? null : available.Max();
	}

	private static bool IsMeaningfulSensorValue(string type, double value) => type switch
	{
		"Temperature" => value > 1.0,
		"Fan" or "Clock" or "Power" => value > 0.0,
		_ => true
	};

	private static uint GetLogicalProcessorCount()
	{
		uint count = GetActiveProcessorCount(ushort.MaxValue);
		return count > 0 ? count : (uint)Environment.ProcessorCount;
	}

	private static string FormatPercent(double? value) => value.HasValue ? FormatNumber(value.Value, "0.0") + "%" : "Unavailable";

	private static string FormatTemperature(double? value) => value.HasValue ? FormatNumber(value.Value, "0") + " °C" : "Unavailable";

	private static string FormatFan(double? value) => value.HasValue ? FormatNumber(value.Value, "N0") + " RPM" : "Unavailable";

	private static string FormatNumber(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

	internal static string FormatBytes(ulong bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
		double value = bytes;
		int unit = 0;
		while (value >= 1024.0 && unit < units.Length - 1)
		{
			value /= 1024.0;
			unit++;
		}
		return value.ToString(value >= 100 || unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
	}

	private static string JoinDetails(params string?[] values)
	{
		string result = string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value))!);
		return string.IsNullOrWhiteSpace(result) ? "Unavailable" : result;
	}

	private static bool NamesOverlap(string left, string right)
	{
		string normalizedLeft = NormalizeName(left);
		string normalizedRight = NormalizeName(right);
		return normalizedLeft.Length >= 5 && normalizedRight.Length >= 5 &&
			(normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal));
	}

	private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

	private static ushort ConvertToUInt16(object? value)
	{
		try { return value == null ? (ushort)0 : Convert.ToUInt16(value, CultureInfo.InvariantCulture); }
		catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return 0; }
	}

	private static ulong ConvertToUInt64(object? value)
	{
		try { return value == null ? 0UL : Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
		catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return 0UL; }
	}

	private static bool IsExpectedHardwareException(Exception exception) =>
		exception is InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.IO.IOException or ManagementException or COMException or System.ComponentModel.Win32Exception or DllNotFoundException or TypeInitializationException or TypeLoadException;

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

	[DllImport("kernel32.dll")]
	private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
