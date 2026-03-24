using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Crypto.Digests;
using Transfarr.Shared.Utils;

namespace Transfarr.Shared.Hashing;

public static class TigerTreeHash
{
    private const int BlockSize = 1024;

    public static string ComputeTTH(Stream stream)
    {
        var leafHashes = new List<byte[]>();
        var buffer = new byte[BlockSize];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, BlockSize)) > 0)
        {
            var digest = new TigerDigest();
            digest.Update(0x00); // Leaf prefix
            digest.BlockUpdate(buffer, 0, bytesRead);
            var hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);
            leafHashes.Add(hash);
        }

        if (leafHashes.Count == 0)
        {
            // Empty file TTH
            var digest = new TigerDigest();
            var hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);
            return Base32.Encode(hash);
        }

        var rootHash = ComputeRoot(leafHashes);
        return Base32.Encode(rootHash);
    }

    private static byte[] ComputeRoot(List<byte[]> hashes)
    {
        while (hashes.Count > 1)
        {
            var parentHashes = new List<byte[]>();
            for (int i = 0; i < hashes.Count; i += 2)
            {
                if (i + 1 < hashes.Count)
                {
                    var digest = new TigerDigest();
                    digest.Update(0x01); // Internal node prefix
                    digest.BlockUpdate(hashes[i], 0, hashes[i].Length);
                    digest.BlockUpdate(hashes[i + 1], 0, hashes[i + 1].Length);
                    var parentHash = new byte[digest.GetDigestSize()];
                    digest.DoFinal(parentHash, 0);
                    parentHashes.Add(parentHash);
                }
                else
                {
                    // Unbalanced tree: promote the last hash
                    parentHashes.Add(hashes[i]);
                }
            }
            hashes = parentHashes;
        }
        return hashes[0];
    }
}
