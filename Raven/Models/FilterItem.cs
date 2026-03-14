public class FilterItem
{
    public string DisplayName { get; set; }
    public object Value { get; set; }

    public FilterItem(string displayName, object value)
    {
        DisplayName = displayName;
        Value = value;
    }
}
