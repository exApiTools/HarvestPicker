using Newtonsoft.Json.Linq;

namespace HarvestPicker.Api.Response;

public class ResponseRoot
{
    public ResponseData data { get; set; }
    public JToken errors { get; set; }
}