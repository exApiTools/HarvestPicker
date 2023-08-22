namespace HarvestPicker.Api.Request;

public class RequestRoot
{
    public string operationName { get; set; }
    public Variables variables { get; set; }
    public string query { get; set; }
}