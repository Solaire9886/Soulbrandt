namespace RsxShaderMatch;

/// <summary>One decoded library vertex program: dev name, parsed name features, microcode
/// fingerprint, and the header's own attribute-input mask (bit n = input attribute n read).</summary>
sealed record LibVp(string Name, LibVpName Features, RsxVp.Fingerprint Fp, int HeaderInstrCount, uint AttrInMask);
