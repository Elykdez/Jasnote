namespace Jesnote.Core;

public enum JsonNodeType : byte
{
    Undefined = 0,
    Array = 1,
    Boolean = 2,
    Null = 3,
    Number = 4,
    Object = 5,
    String = 6,
}

public static class JsonNodeTypeExtensions
{
    public static string DisplayName(this JsonNodeType t) => Localization.JsonNodeTypeName(t);

    public static bool IsBranch(this JsonNodeType t) =>
        t == JsonNodeType.Array || t == JsonNodeType.Object;
}
