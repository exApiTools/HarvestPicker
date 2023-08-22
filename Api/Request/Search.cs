namespace HarvestPicker.Api.Request;

public class Search
{
    public string league { get; set; }
    public int offSet { get; set; }
    public string searchString { get; set; }
    public string tag { get; set; }
    public int quantityMin { get; set; }
}