namespace LogixTagRelay.Logix;

/// <summary>Documented Logix string layout: DINT LEN then SINT DATA[N], with native tail alignment.</summary>
public static class LogixStringBuffer
{
    public static string Decode(FieldMap field, byte[] nativeBuffer)
    {
        Validate(field, nativeBuffer);
        return (string)PlcTypes.Decode(field, nativeBuffer);
    }
    public static byte[] Encode(FieldMap field, string value, byte[] nativeReadback)
    {
        Validate(field, nativeReadback);
        var result = nativeReadback.ToArray();
        PlcTypes.Encode(field, value, result); // Preserves native tail padding rather than inventing its contents.
        return result;
    }
    private static void Validate(FieldMap field, byte[] buffer)
    {
        if (field.Type != PlcType.STRING || buffer.Length != (field.WireBytes + 3) / 4 * 4)
            throw new FormatException("Native STRING buffer does not match the declared capacity/alignment. Refresh the contract.");
    }
}
