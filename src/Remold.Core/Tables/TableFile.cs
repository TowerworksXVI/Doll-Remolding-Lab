using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Tables;

/// <summary>
/// Loads a GFL2 design table (<c>Table/*.bytes</c>) into its row messages. Layout: a 4-byte header word,
/// then a protobuf message whose <b>field #1</b> appears once as a leading varint count and then repeats
/// as length-delimited row submessages — only the length-delimited occurrences are rows.
/// </summary>
public static class TableFile
{
    public const int HeaderBytes = 4;
    public const int RowField = 1;

    public static List<PbMessage> ReadRows(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var top = PbMessage.Parse(bytes.AsSpan(HeaderBytes));
        return top.Field(RowField)
                  .Where(v => v.Wire == WireType.Len && v.Bytes is not null)
                  .Select(v => PbMessage.Parse(v.Bytes!))
                  .ToList();
    }
}
