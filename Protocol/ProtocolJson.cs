using System.Text.Json;

namespace CommunicateTheSpire2.Protocol;

public static class ProtocolJson
{
	public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		WriteIndented = false
	};

	public static string Serialize<T>(T obj)
	{
		return JsonSerializer.Serialize(obj, Options);
	}
}

