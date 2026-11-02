namespace LogixTagRelay.Logix;

/// <summary>Documented Logix string layout: DINT LEN then SINT DATA[N], with native tail alignment.</summary>
public static class LogixStringBuffer
{
    /// <summary>Decodes native LEN and DATA after validating the declared capacity and available buffer.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="nativeBuffer">Native STRING read buffer containing LEN, DATA and any tail padding.</param>
    public static string Decode(FieldMap field, byte[] nativeBuffer)
    {
        Validate(field, nativeBuffer);
        return (string)PlcTypes.Decode(field, nativeBuffer);
    }
    /// <summary>Copies native readback and updates only LEN and used DATA, preserving the remaining native tail.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="nativeReadback">Current native STRING buffer whose unused bytes must be preserved.</param>
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
