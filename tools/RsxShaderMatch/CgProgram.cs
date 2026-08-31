namespace RsxShaderMatch;

/// <summary>
/// Parser for Demon's Souls' <c>.fpo</c>/<c>.vpo</c> shader-library entries
/// (the files inside <c>shader/ds_flver.shaderbnd</c> etc., extracted to
/// <c>mounted/shader/&lt;container&gt;/</c>).
///
/// Layout, worked out by inspection and cross-checked against RPCS3's
/// <c>CgBinaryProgram.h</c> (see tools/RsxShaderMatch/README.md):
///
///   0x00  u32 be   magic         0xFF0F0F11 (.fpo) / 0xFF0F0011 (.vpo)
///   0x04  u32 be   cgOffset      byte offset to the embedded CgBinaryProgram (0x30 in every file seen)
///   0x08  u32 be   unknown       (0x00020000)
///   0x0C  u32 be   unknown       (0x00000001)
///   0x10 .. cgOffset             zeros + a small u32 table whose meaning isn't nailed down yet
///
///   At cgOffset, a standard big-endian Sony CgBinaryProgram:
///     +0x00 u32  profile             7004 = fragment (0x1B5C), 7003 = vertex (0x1B5B)
///     +0x04 u32  binaryFormatRevision
///     +0x08 u32  totalSize           == fileLength - cgOffset
///     +0x0C u32  parameterCount      0 in every DeS file - the parameter/name table is stripped
///     +0x10 u32  parameterArray      offset (0)
///     +0x14 u32  program             offset (from CgBinaryProgram start) to the Cg{Fragment,Vertex}Program struct
///     +0x18 u32  ucodeSize           bytes of microcode
///     +0x1C u32  ucode               offset (from CgBinaryProgram start) to the raw microcode
///
///   At cgOffset + program, big-endian CgBinaryFragmentProgram. Only instructionCount
///   is confirmed; the fields after it are laid out per RPCS3's struct but their decoded
///   values here still look off (texCoords2D reads 0xFFFD on the HDR shader), so the
///   struct base offset or field widths need one more pass before trusting them. None of
///   them are load-bearing for matching - texture units come from decoding TEX ops.
///     +0x00 u32  instructionCount    == ucodeSize / 16  (confirmed)
///     +0x04 u32  attributeInputMask
///     +0x08 u32  partialTexType
///     +0x0C u16  texCoordsInputMask  (bit n = texcoord n is read)
///     +0x0E u16  texCoords2D         (bit n = texcoord n is 2D)
///     +0x10 u16  texCoordsCentroid
///     +0x12 u8   registerCount
///     +0x13 u8   outputFromH0        (0 = final colour from R0, 1 = from H0)
///     +0x14 u8   depthReplace
///     +0x15 u8   pixelKill
///
///   ...or big-endian CgBinaryVertexProgram:
///     +0x00 u32  instructionCount
///     +0x04 u32  instructionSlot     load address
///     +0x08 u32  registerCount
///     +0x0C u32  attributeInputMask
///     +0x10 u32  attributeOutputMask
///     +0x14 u32  userClipMask
///
///   At cgOffset + ucode, <c>ucodeSize</c> bytes of raw RSX microcode, stored as
///   big-endian u32 words (PS3 native). Instruction decoding additionally needs a
///   16-bit halfword swap per word - not done here, that's the decoder's job.
/// </summary>
sealed record CgProgram
{
    public enum ShaderKind { Fragment, Vertex }

    public required string Path { get; init; }
    public required string Name { get; init; }          // file name without extension = the dev shader name
    public required long FileLength { get; init; }
    public required uint Magic { get; init; }
    public required uint CgOffset { get; init; }

    public required ShaderKind Kind { get; init; }
    public required uint Profile { get; init; }
    public required uint BinaryFormatRevision { get; init; }
    public required uint TotalSize { get; init; }
    public required uint ParameterCount { get; init; }

    public required uint InstructionCount { get; init; }
    public required uint UcodeSize { get; init; }
    public required uint UcodeFileOffset { get; init; }  // absolute offset into the file
    public required byte[] Ucode { get; init; }

    // Fragment-only metadata (zero/ignored for vertex programs).
    public ushort FpTexCoordsInputMask { get; init; }
    public ushort FpTexCoords2D { get; init; }
    public ushort FpTexCoordsCentroid { get; init; }
    public byte FpRegisterCount { get; init; }
    public byte FpOutputFromH0 { get; init; }
    public byte FpDepthReplace { get; init; }
    public byte FpPixelKill { get; init; }

    // Vertex-only metadata.
    public uint VpInstructionSlot { get; init; }
    public uint VpRegisterCount { get; init; }
    public uint VpAttributeInputMask { get; init; }
    public uint VpAttributeOutputMask { get; init; }
    public uint VpUserClipMask { get; init; }

    const uint ProfileFragment = 7004;
    const uint ProfileVertex = 7003;

    /// <summary>Problems found while parsing that don't prevent producing a result but are worth flagging.</summary>
    public List<string> Warnings { get; } = new();

    public static CgProgram Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 0x40)
            throw new InvalidDataException($"{path}: too small to be a shader program ({bytes.Length} bytes)");

        uint magic = ReadU32Be(bytes, 0x00);
        if ((magic & 0xFF000000u) != 0xFF000000u)
            throw new InvalidDataException($"{path}: unexpected magic 0x{magic:X8}");

        uint cgOffset = ReadU32Be(bytes, 0x04);
        if (cgOffset + 0x20 > bytes.Length)
            throw new InvalidDataException($"{path}: CgBinaryProgram offset 0x{cgOffset:X} out of range");

        uint profile = ReadU32Be(bytes, cgOffset + 0x00);
        uint revision = ReadU32Be(bytes, cgOffset + 0x04);
        uint totalSize = ReadU32Be(bytes, cgOffset + 0x08);
        uint parameterCount = ReadU32Be(bytes, cgOffset + 0x0C);
        uint programOff = ReadU32Be(bytes, cgOffset + 0x14);
        uint ucodeSize = ReadU32Be(bytes, cgOffset + 0x18);
        uint ucodeOff = ReadU32Be(bytes, cgOffset + 0x1C);

        ShaderKind kind = profile switch
        {
            ProfileFragment => ShaderKind.Fragment,
            ProfileVertex => ShaderKind.Vertex,
            _ => throw new InvalidDataException($"{path}: unknown Cg profile {profile}"),
        };

        uint progAbs = cgOffset + programOff;
        uint ucodeAbs = cgOffset + ucodeOff;
        if (ucodeAbs + ucodeSize > bytes.Length)
            throw new InvalidDataException($"{path}: ucode [0x{ucodeAbs:X}, +0x{ucodeSize:X}) out of range (file 0x{bytes.Length:X})");

        var prog = new CgProgram
        {
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            FileLength = bytes.Length,
            Magic = magic,
            CgOffset = cgOffset,
            Kind = kind,
            Profile = profile,
            BinaryFormatRevision = revision,
            TotalSize = totalSize,
            ParameterCount = parameterCount,
            InstructionCount = ReadU32Be(bytes, progAbs + 0x00),
            UcodeSize = ucodeSize,
            UcodeFileOffset = ucodeAbs,
            Ucode = bytes[(int)ucodeAbs..(int)(ucodeAbs + ucodeSize)],
        };

        if (kind == ShaderKind.Fragment)
        {
            prog = prog with
            {
                FpTexCoordsInputMask = ReadU16Be(bytes, progAbs + 0x0C),
                FpTexCoords2D = ReadU16Be(bytes, progAbs + 0x0E),
                FpTexCoordsCentroid = ReadU16Be(bytes, progAbs + 0x10),
                FpRegisterCount = bytes[progAbs + 0x12],
                FpOutputFromH0 = bytes[progAbs + 0x13],
                FpDepthReplace = bytes[progAbs + 0x14],
                FpPixelKill = bytes[progAbs + 0x15],
            };
        }
        else
        {
            prog = prog with
            {
                VpInstructionSlot = ReadU32Be(bytes, progAbs + 0x04),
                VpRegisterCount = ReadU32Be(bytes, progAbs + 0x08),
                VpAttributeInputMask = ReadU32Be(bytes, progAbs + 0x0C),
                VpAttributeOutputMask = ReadU32Be(bytes, progAbs + 0x10),
                VpUserClipMask = ReadU32Be(bytes, progAbs + 0x14),
            };
        }

        // Invariants we expect to hold for every real DeS shader. Recorded, not thrown -
        // the point of the first corpus sweep is to see whether any file breaks them.
        if (totalSize != bytes.Length - cgOffset)
            prog.Warnings.Add($"totalSize 0x{totalSize:X} != fileLength-cgOffset 0x{bytes.Length - cgOffset:X}");
        if (ucodeSize % 16 != 0)
            prog.Warnings.Add($"ucodeSize 0x{ucodeSize:X} not a multiple of 16");
        if (prog.InstructionCount * 16 != ucodeSize)
            prog.Warnings.Add($"instructionCount {prog.InstructionCount} * 16 != ucodeSize 0x{ucodeSize:X}");
        // Fragment programs ship parameterCount 0 (whole table gone). Vertex programs keep
        // the table structurally - entry count, types (1047 = float3, 1048 = float4) and the
        // embedded default-value floats - but every CgBinaryParameter.name offset is 0, so
        // the register names are gone just the same. Only flag the fragment case.
        if (kind == ShaderKind.Fragment && parameterCount != 0)
            prog.Warnings.Add($"fragment parameterCount {parameterCount} (expected 0 - stripped param table)");

        return prog;
    }

    static uint ReadU32Be(byte[] b, uint o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    static uint ReadU32Be(byte[] b, int o) => ReadU32Be(b, (uint)o);

    static ushort ReadU16Be(byte[] b, uint o) => (ushort)((b[o] << 8) | b[o + 1]);
}
