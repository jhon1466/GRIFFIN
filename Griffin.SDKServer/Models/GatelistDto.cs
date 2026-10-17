using System.Text.Json.Serialization;

namespace Griffin.SDKServer.Models
{
    public class GatelistDto
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public List<string> Data { get; set; } = [];
    }
}
