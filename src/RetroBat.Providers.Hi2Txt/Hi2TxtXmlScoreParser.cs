using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroBat.Domain.Models;

namespace RetroBat.Providers.Hi2Txt;

internal static class Hi2TxtXmlScoreParser
{
    public static IReadOnlyList<HiscoreEntry> ParseScores(string saveFile, string descriptorPath)
    {
        var descriptor = Hi2TxtDescriptor.Load(descriptorPath);
        var originalBytes = File.ReadAllBytes(saveFile);
        var structureKey = Path.GetExtension(saveFile);
        if (string.IsNullOrWhiteSpace(structureKey))
        {
            structureKey = Path.GetFileName(saveFile);
        }

        var structures = descriptor.MatchStructures(structureKey, originalBytes.Length).ToList();
        if (structures.Count == 0)
        {
            return Array.Empty<HiscoreEntry>();
        }

        var descriptorName = Path.GetFileNameWithoutExtension(descriptorPath);
        IReadOnlyList<HiscoreEntry> bestScores = Array.Empty<HiscoreEntry>();
        var bestQuality = int.MinValue;
        const int structureReplacementThreshold = 200;

        foreach (var structure in structures)
        {
            var bytes = ApplyStructureTransforms(originalBytes, structure);
            var dataSeries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            var loopIndexes = new List<int>(capacity: 8);
            var parseState = new ParseState();
            ProcessNodes(structure.Nodes, bytes, ref offset, descriptor, dataSeries, loopIndexes, parseState);

            var scores = PostProcessKnownEdgeCases(
                descriptorName,
                BuildEntries(descriptor, structure, dataSeries));
            var quality = EvaluateScoreQuality(scores);
            if (bestScores.Count == 0)
            {
                bestQuality = quality;
                bestScores = scores;
                continue;
            }

            if (quality > bestQuality + structureReplacementThreshold)
            {
                bestQuality = quality;
                bestScores = scores;
            }
        }

        return bestScores;
    }

    private static IReadOnlyList<HiscoreEntry> PostProcessKnownEdgeCases(string descriptorName, IReadOnlyList<HiscoreEntry> scores)
    {
        if (scores.Count == 0)
        {
            return scores;
        }

        if (string.Equals(descriptorName, "tigeroad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptorName, "pang3", StringComparison.OrdinalIgnoreCase))
        {
            return scores
                .OrderByDescending(static entry => TryParseScoreForOrdering(entry.Score, out var score) ? score : long.MinValue)
                .ThenBy(static entry => entry.Rank, StringComparer.OrdinalIgnoreCase)
                .Select((entry, index) => new HiscoreEntry
                {
                    Rank = (index + 1).ToString(CultureInfo.InvariantCulture),
                    Score = entry.Score,
                    Name = entry.Name
                })
                .ToList();
        }

        if (string.Equals(descriptorName, "paradise", StringComparison.OrdinalIgnoreCase))
        {
            return scores
                .Select(static entry => new HiscoreEntry
                {
                    Rank = entry.Rank,
                    Score = TrimNumericLeadingZeros(entry.Score),
                    Name = string.Empty
                })
                .ToList();
        }

        return scores;
    }

    private static bool TryParseScoreForOrdering(string? score, out long value)
    {
        value = 0;
        var cleaned = Regex.Replace(score ?? string.Empty, @"[^0-9\-]", string.Empty);
        return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int EvaluateScoreQuality(IReadOnlyList<HiscoreEntry> scores)
    {
        if (scores.Count == 0)
        {
            return 0;
        }

        var quality = scores.Count * 1000;
        var nonZeroScores = 0;
        var descendingPairs = 0;
        var cleanNames = 0;

        for (var i = 0; i < scores.Count; i++)
        {
            if (long.TryParse(scores[i].Score, out var parsedScore) && parsedScore > 0)
            {
                nonZeroScores++;
            }

            var name = scores[i].Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) &&
                name[0] is not '\'' and not ' ' &&
                name.Any(static ch => char.IsLetterOrDigit(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.OtherLetter))
            {
                cleanNames++;
            }

            if (i == 0)
            {
                continue;
            }

            if (long.TryParse(scores[i - 1].Score, out var previous) &&
                long.TryParse(scores[i].Score, out var current) &&
                previous >= current)
            {
                descendingPairs++;
            }
        }

        quality += nonZeroScores * 50;
        quality += descendingPairs * 120;
        quality += cleanNames * 10;
        return quality;
    }

    private static void ProcessNodes(
        IReadOnlyList<Hi2TxtNode> nodes,
        byte[] bytes,
        ref int offset,
        Hi2TxtDescriptor descriptor,
        Dictionary<string, List<string>> dataSeries,
        List<int> loopIndexes,
        ParseState parseState)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Hi2TxtLoopNode loop:
                    ProcessLoop(loop, bytes, ref offset, descriptor, dataSeries, loopIndexes, parseState);
                    break;
                case Hi2TxtElementNode element:
                    var value = ReadElementValue(element, bytes, ref offset, descriptor);
                    var index = ResolveElementIndex(element, value, loopIndexes, dataSeries, descriptor, parseState);
                    var storageIndex = ResolveStorageIndex(element, index, loopIndexes);
                    SetSeriesValue(dataSeries, element.Id, storageIndex, GetStoredElementValue(element, value, index));
                    parseState.LastAssignedIndex = index;
                    break;
            }
        }
    }

    private static void ProcessLoop(
        Hi2TxtLoopNode loop,
        byte[] bytes,
        ref int offset,
        Hi2TxtDescriptor descriptor,
        Dictionary<string, List<string>> dataSeries,
        List<int> loopIndexes,
        ParseState parseState)
    {
        if ((loop.SkipFirstBytes > 0 || loop.SkipLastBytes > 0) &&
            loop.Nodes.All(static node => node is Hi2TxtElementNode))
        {
            ProcessSimplePartialLoop(loop, bytes, ref offset, descriptor, dataSeries, loopIndexes, parseState);
            return;
        }

        for (var i = 0; i < loop.Count; i++)
        {
            if (i == 0)
            {
                offset += loop.SkipFirstBytes;
            }

            loopIndexes.Add(loop.Start + i);
            ProcessNodes(loop.Nodes, bytes, ref offset, descriptor, dataSeries, loopIndexes, parseState);
            loopIndexes.RemoveAt(loopIndexes.Count - 1);

            if (i == loop.Count - 1)
            {
                offset += loop.SkipLastBytes;
            }
        }
    }

    private static void ProcessSimplePartialLoop(
        Hi2TxtLoopNode loop,
        byte[] bytes,
        ref int offset,
        Hi2TxtDescriptor descriptor,
        Dictionary<string, List<string>> dataSeries,
        List<int> loopIndexes,
        ParseState parseState)
    {
        var elements = loop.Nodes.Cast<Hi2TxtElementNode>().ToArray();
        var logicalRowSize = elements.Sum(static element => element.Size);

        for (var i = 0; i < loop.Count; i++)
        {
            var readStart = i == 0 ? loop.SkipFirstBytes : 0;
            var readEnd = logicalRowSize - (i == loop.Count - 1 ? loop.SkipLastBytes : 0);
            var logicalOffset = 0;

            loopIndexes.Add(loop.Start + i);
            foreach (var element in elements)
            {
                var logicalElementStart = logicalOffset;
                var logicalElementEnd = logicalOffset + element.Size;
                var sliceStart = Math.Max(logicalElementStart, readStart);
                var sliceEnd = Math.Min(logicalElementEnd, readEnd);
                var readSize = Math.Max(sliceEnd - sliceStart, 0);

                if (readSize > 0)
                {
                    var value = ReadElementValuePartial(element, bytes, ref offset, readSize, descriptor);
                    var index = ResolveElementIndex(element, value, loopIndexes, dataSeries, descriptor, parseState);
                    var storageIndex = ResolveStorageIndex(element, index, loopIndexes);
                    SetSeriesValue(dataSeries, element.Id, storageIndex, GetStoredElementValue(element, value, index));
                    parseState.LastAssignedIndex = index;
                }

                logicalOffset += element.Size;
            }

            loopIndexes.RemoveAt(loopIndexes.Count - 1);
        }
    }

    private static IReadOnlyList<HiscoreEntry> BuildEntries(
        Hi2TxtDescriptor descriptor,
        Hi2TxtStructure structure,
        Dictionary<string, List<string>> dataSeries)
    {
        var output = descriptor.GetOutput(structure.OutputId);
        var table = SelectBestScoreTable(descriptor, output, dataSeries) ?? descriptor.CreateFallbackTable();

        var rowCount = ResolveRowCount(table, dataSeries);
        var orderedIndexes = BuildOrderedIndexes(descriptor, table, dataSeries, rowCount);
        var results = new List<HiscoreEntry>(orderedIndexes.Count);
        for (var orderedIndex = 0; orderedIndex < orderedIndexes.Count; orderedIndex++)
        {
            var dataIndex = orderedIndexes[orderedIndex];
            var visibleIndex = results.Count;
            var rendered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                rendered[column.Id] = descriptor.RenderColumn(column, dataSeries, dataIndex, orderedIndex);
            }

            if (table.ShouldIgnoreRow(rendered, results.Count))
            {
                continue;
            }

            var score = GetCanonicalValue(rendered, "SCORE");
            var name = GetCanonicalValue(rendered, "NAME");
            if (string.IsNullOrWhiteSpace(score) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            results.Add(new HiscoreEntry
            {
                Rank = NormalizeRank(GetCanonicalValue(rendered, "RANK"), visibleIndex),
                Score = score,
                Name = name
            });

            if (table.LinesMax >= 0 && results.Count >= table.LinesMax)
            {
                break;
            }
        }

        return results;
    }

    private static Hi2TxtOutputTable? SelectBestScoreTable(Hi2TxtDescriptor descriptor, Hi2TxtOutput? output, Dictionary<string, List<string>> dataSeries)
    {
        if (output == null)
        {
            return null;
        }

        var naturalTable = output.SelectScoreTable();
        if (TableHasUsableData(descriptor, naturalTable, dataSeries))
        {
            return naturalTable;
        }

        var tables = output.Tables
            .Select(table => new
            {
                Table = table,
                MatchCount = table.Columns.Count(column => HasDataForColumn(descriptor, column, dataSeries)),
                HasScore = table.Columns.Any(column => IsCanonicalColumn(column.Id, "SCORE")),
                HasName = table.Columns.Any(column => IsCanonicalColumn(column.Id, "NAME"))
            })
            .OrderByDescending(static entry => entry.HasScore)
            .ThenByDescending(static entry => entry.HasScore && entry.HasName)
            .ThenByDescending(static entry => entry.MatchCount)
            .ToList();

        return tables.FirstOrDefault(static entry => entry.MatchCount > 0)?.Table
            ?? naturalTable;
    }

    private static bool TableHasUsableData(Hi2TxtDescriptor descriptor, Hi2TxtOutputTable table, IReadOnlyDictionary<string, List<string>> dataSeries)
    {
        return table.Columns
            .Where(column => IsCanonicalColumn(column.Id, "SCORE") || IsCanonicalColumn(column.Id, "NAME"))
            .Any(column => HasDataForColumn(descriptor, column, dataSeries));
    }

    private static bool HasDataForColumn(Hi2TxtDescriptor descriptor, Hi2TxtColumn column, IReadOnlyDictionary<string, List<string>> dataSeries)
    {
        var source = column.Source ?? column.Id;
        if (string.Equals(source, "index", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "unsorted_index", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dataSeries.ContainsKey(source) || dataSeries.ContainsKey(column.Id))
        {
            return true;
        }

        var rowCount = dataSeries.Count == 0 ? 1 : dataSeries.Values.Max(static values => values.Count);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var rendered = descriptor.RenderColumn(column, dataSeries, rowIndex, rowIndex);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCanonicalValue(IReadOnlyDictionary<string, string> row, string key)
    {
        if (row.TryGetValue(key, out var directValue))
        {
            return directValue;
        }

        var match = row.FirstOrDefault(entry => IsCanonicalColumn(entry.Key, key));
        return match.Equals(default(KeyValuePair<string, string>)) ? string.Empty : match.Value;
    }

    private static bool IsCanonicalColumn(string? candidate, string key)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase) ||
               candidate.EndsWith(" " + key, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> BuildOrderedIndexes(
        Hi2TxtDescriptor descriptor,
        Hi2TxtOutputTable table,
        Dictionary<string, List<string>> dataSeries,
        int rowCount)
    {
        var orderedIndexes = Enumerable.Range(0, rowCount).ToList();
        if (string.IsNullOrWhiteSpace(table.SortColumn) || orderedIndexes.Count <= 1)
        {
            return orderedIndexes;
        }

        var sortColumn = table.Columns.FirstOrDefault(column =>
            string.Equals(column.Id, table.SortColumn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column.Source, table.SortColumn, StringComparison.OrdinalIgnoreCase));
        if (sortColumn == null)
        {
            return orderedIndexes;
        }

        orderedIndexes.Sort((left, right) =>
        {
            var leftValue = descriptor.RenderColumn(sortColumn, dataSeries, left, left);
            var rightValue = descriptor.RenderColumn(sortColumn, dataSeries, right, right);
            leftValue = descriptor.ApplyFormats(leftValue, table.SortFormats, dataSeries, left, left);
            rightValue = descriptor.ApplyFormats(rightValue, table.SortFormats, dataSeries, right, right);

            var comparison = CompareSortValues(leftValue, rightValue);
            return table.SortDescending ? -comparison : comparison;
        });

        return orderedIndexes;
    }

    private static int CompareSortValues(string left, string right)
    {
        if (TryParseNumeric(left, out var leftNumeric) && TryParseNumeric(right, out var rightNumeric))
        {
            return leftNumeric.CompareTo(rightNumeric);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseNumeric(string? text, out decimal value)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"[^0-9\.\-]", string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static int ResolveRowCount(Hi2TxtOutputTable table, Dictionary<string, List<string>> dataSeries)
    {
        var max = 0;
        foreach (var column in table.Columns)
        {
            var source = column.Source ?? column.Id;
            if (string.Equals(source, "index", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "unsorted_index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dataSeries.TryGetValue(source, out var values))
            {
                max = Math.Max(max, values.Count);
            }
            else if (dataSeries.TryGetValue(column.Id, out values))
            {
                max = Math.Max(max, values.Count);
            }
        }

        if (max == 0 && dataSeries.TryGetValue("SCORE", out var scores))
        {
            max = scores.Count;
        }

        if (max == 0 && dataSeries.TryGetValue("NAME", out var names))
        {
            max = names.Count;
        }

        if (max == 0 && dataSeries.Count > 0)
        {
            max = dataSeries.Values.Max(static values => values.Count);
        }

        return max;
    }

    private static string GetValue(Dictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string NormalizeRank(string rank, int index)
    {
        if (string.IsNullOrWhiteSpace(rank))
        {
            return (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (int.TryParse(rank, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        return rank;
    }

    private static void SetSeriesValue(Dictionary<string, List<string>> dataSeries, string id, int index, string value)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!dataSeries.TryGetValue(id, out var values))
        {
            values = new List<string>();
            dataSeries[id] = values;
        }

        while (values.Count <= index)
        {
            values.Add(string.Empty);
        }

        values[index] = value;
    }

    private static int ResolveElementIndex(
        Hi2TxtElementNode element,
        string value,
        IReadOnlyList<int> loopIndexes,
        IReadOnlyDictionary<string, List<string>> dataSeries,
        Hi2TxtDescriptor descriptor,
        ParseState parseState)
    {
        var baseIndex = 0;
        if (string.IsNullOrWhiteSpace(element.TableIndex))
        {
            baseIndex = loopIndexes.Count > 0 ? loopIndexes[^1] : 0;
        }
        else if (string.Equals(element.TableIndex, "last", StringComparison.OrdinalIgnoreCase))
        {
            baseIndex = parseState.LastAssignedIndex;
        }
        else if (string.Equals(element.TableIndex, "itself", StringComparison.OrdinalIgnoreCase))
        {
            baseIndex = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : 0;
        }
        else if (string.Equals(element.TableIndex, "loop_index", StringComparison.OrdinalIgnoreCase))
        {
            baseIndex = loopIndexes.Count > 0 ? loopIndexes[^1] : 0;
        }
        else if (int.TryParse(element.TableIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            baseIndex = parsed;
        }
        else
        {
            var parts = element.TableIndex.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && dataSeries.TryGetValue(parts[0], out var series))
            {
                var loopIndex = loopIndexes.Count > 0 ? loopIndexes[^1] : 0;
                if (parts.Length > 1 && string.Equals(parts[1], "index_from_value", StringComparison.OrdinalIgnoreCase))
                {
                    for (var i = 0; i < series.Count; i++)
                    {
                        if (int.TryParse(series[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesValue) &&
                            seriesValue == loopIndex)
                        {
                            baseIndex = i;
                            break;
                        }
                    }
                }
                else if (loopIndex >= 0 && loopIndex < series.Count &&
                         int.TryParse(series[loopIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesIndex))
                {
                    baseIndex = seriesIndex;
                }
            }
        }

        var rendered = descriptor.ApplyFormats(
            baseIndex.ToString(CultureInfo.InvariantCulture),
            element.TableIndexFormats,
            EmptyDataSeries.Instance,
            baseIndex,
            baseIndex);

        return int.TryParse(rendered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var formatted)
            ? Math.Max(formatted, 0)
            : decimal.TryParse(rendered, NumberStyles.Number, CultureInfo.InvariantCulture, out var formattedDecimal)
                ? Math.Max((int)decimal.Truncate(formattedDecimal), 0)
                : Math.Max(baseIndex, 0);
    }

    private static bool PreservesTrailingCharacters(string formatId)
    {
        return formatId.StartsWith("PadL", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("PadR", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("TrimL", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("TrimR", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("Trim", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("Prefix", StringComparison.OrdinalIgnoreCase) ||
               formatId.StartsWith("Suffix", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveStorageIndex(Hi2TxtElementNode element, int resolvedIndex, IReadOnlyList<int> loopIndexes)
    {
        return string.Equals(element.TableIndex, "itself", StringComparison.OrdinalIgnoreCase) &&
               element.TableIndexFormats.Count > 0 &&
               element.Formats.Any(static format => string.Equals(format.Id, "LoopIndex", StringComparison.OrdinalIgnoreCase)) &&
               loopIndexes.Count > 0
            ? loopIndexes[^1]
            : resolvedIndex;
    }

    private static string GetStoredElementValue(Hi2TxtElementNode element, string value, int index)
    {
        return string.Equals(element.TableIndex, "itself", StringComparison.OrdinalIgnoreCase) &&
               element.TableIndexFormats.Count > 0 &&
               element.Formats.Any(static format => string.Equals(format.Id, "LoopIndex", StringComparison.OrdinalIgnoreCase))
            ? index.ToString(CultureInfo.InvariantCulture)
            : value;
    }

    private static string ReadElementValue(
        Hi2TxtElementNode element,
        byte[] bytes,
        ref int offset,
        Hi2TxtDescriptor descriptor)
    {
        if (element.Size <= 0 || offset >= bytes.Length)
        {
            offset = bytes.Length;
            return string.Empty;
        }

        var availableSize = Math.Min(element.Size, bytes.Length - offset);
        var slice = bytes[offset..(offset + availableSize)];
        offset += element.Size;

        var value = element.Type switch
        {
            "text" => DecodeText(ApplyTransforms(slice, element, descriptor), element, descriptor),
            "int" => DecodeInt(ApplyTransforms(slice, element, descriptor), element),
            "raw" => Convert.ToHexString(slice),
            _ => string.Empty
        };

        return descriptor.ApplyFormats(value, element.Formats, EmptyDataSeries.Instance, 0, 0);
    }

    private static string ReadElementValuePartial(
        Hi2TxtElementNode element,
        byte[] bytes,
        ref int offset,
        int readSize,
        Hi2TxtDescriptor descriptor)
    {
        if (readSize <= 0 || offset >= bytes.Length)
        {
            return string.Empty;
        }

        var availableSize = Math.Min(readSize, bytes.Length - offset);
        var slice = bytes[offset..(offset + availableSize)];
        offset += availableSize;

        var value = element.Type switch
        {
            "text" => DecodeText(ApplyTransforms(slice, element, descriptor), element, descriptor),
            "int" => DecodeInt(ApplyTransforms(slice, element, descriptor), element),
            "raw" => Convert.ToHexString(slice),
            _ => string.Empty
        };

        return descriptor.ApplyFormats(value, element.Formats, EmptyDataSeries.Instance, 0, 0);
    }

    private static string DecodeText(byte[] source, Hi2TxtElementNode element, Hi2TxtDescriptor descriptor)
    {
        if (source.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var symbol in SplitSymbols(source, element))
        {
            foreach (var charsetId in element.CharsetIds)
            {
                if (descriptor.TryMapCharset(charsetId, symbol, out var mapped))
                {
                    builder.Append(mapped);
                    goto SymbolHandled;
                }
            }

            var value = (symbol / Math.Max(element.AsciiStep, 1)) + element.AsciiOffset;
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
            }
            else if (symbol is >= 32 and <= 126)
            {
                builder.Append((char)symbol);
            }
            else if (value == 0 || symbol == 0)
            {
                continue;
            }

        SymbolHandled:
            ;
        }

        return builder.ToString();
    }

    private static string DecodeInt(byte[] source, Hi2TxtElementNode element)
    {
        if (source.Length == 0)
        {
            return "0";
        }

        if (element.NumberBase == 16)
        {
            if (HasInvalidPackedDecimalNibble(source))
            {
                return "0";
            }

            return TrimNumericLeadingZeros(Convert.ToHexString(source));
        }

        if (element.NumberBase == 256)
        {
            return TrimNumericLeadingZeros(ConvertFromBase(source, element.NumberBase));
        }

        long value = 0;
        foreach (var current in source)
        {
            value = (value * 256) + current;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static bool HasInvalidPackedDecimalNibble(byte[] source)
    {
        foreach (var current in source)
        {
            if ((current & 0x0F) >= 10 || ((current >> 4) & 0x0F) >= 10)
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimNumericLeadingZeros(string text)
    {
        var trimmed = text.TrimStart('0');
        return string.IsNullOrEmpty(trimmed) ? "0" : trimmed;
    }

    private static string ConvertFromBase(byte[] source, int numberBase)
    {
        if (source.Length == 0)
        {
            return "0";
        }

        var value = new System.Numerics.BigInteger(0);
        foreach (var current in source)
        {
            value = (value * 256) + current;
        }

        if (value == 0)
        {
            return "0";
        }

        var baseValue = new System.Numerics.BigInteger(numberBase);
        var builder = new StringBuilder();
        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, baseValue, out var remainder);
            builder.Insert(0, remainder.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }


    private static byte[] ApplyStructureTransforms(byte[] source, Hi2TxtStructure structure)
    {
        var bytes = source.ToArray();
        if (structure.ByteSwap > 1)
        {
            bytes = ApplyByteSwap(bytes, structure.ByteSwap);
        }

        if (structure.BitSwap)
        {
            bytes = ApplyBitSwap(bytes);
        }

        return bytes;
    }

    private static byte[] ApplyTransforms(byte[] source, Hi2TxtElementNode element, Hi2TxtDescriptor descriptor)
    {
        var bytes = source.ToArray();
        if (element.ByteSwap > 1)
        {
            bytes = ApplyByteSwap(bytes, element.ByteSwap);
        }

        if (element.BitSwap)
        {
            bytes = ApplyBitSwap(bytes);
        }

        bytes = ApplyByteSkip(bytes, element.ByteSkip);
        if (element.LittleEndian)
        {
            Array.Reverse(bytes);
        }

        bytes = ApplyByteTrunc(bytes, element.ByteTrunc);
        bytes = ApplyByteTrim(bytes, element.ByteTrim);
        bytes = ApplyNibbleSkip(bytes, element.NibbleSkip);
        bytes = ApplyNibbleTrim(bytes, element.NibbleTrim);

        if (!string.IsNullOrWhiteSpace(element.BitmaskId) &&
            descriptor.TryGetBitmask(element.BitmaskId, out var bitmask))
        {
            bytes = ApplyBitmask(bytes, bitmask);
        }

        return bytes;
    }

    private static byte[] ApplyByteSwap(byte[] source, int unitSize)
    {
        if (unitSize < 2)
        {
            return source;
        }

        var bytes = source.ToArray();
        for (var i = 0; i + unitSize <= bytes.Length; i += unitSize)
        {
            Array.Reverse(bytes, i, unitSize);
        }

        return bytes;
    }

    private static byte[] ApplyBitSwap(byte[] source)
    {
        return source.Select(ReverseBits).ToArray();
    }

    private static byte ReverseBits(byte value)
    {
        var result = 0;
        for (var i = 0; i < 8; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return (byte)result;
    }

    private static byte[] ApplyByteSkip(byte[] source, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return source;
        }

        if (pattern.Equals("even", StringComparison.OrdinalIgnoreCase))
        {
            return source.Where((_, index) => (index + 1) % 2 != 0).ToArray();
        }

        if (pattern.Equals("odd", StringComparison.OrdinalIgnoreCase))
        {
            return source.Where((_, index) => (index + 1) % 2 == 0).ToArray();
        }

        if (pattern.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            byte.TryParse(pattern[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
        {
            return source.Where(b => b != expected).ToArray();
        }

        if (pattern.All(static c => c is '0' or '1'))
        {
            return source
                .Where((_, index) => pattern[index % pattern.Length] == '1')
                .ToArray();
        }

        return source;
    }

    private static byte[] ApplyByteTrunc(byte[] source, byte? stopByte)
    {
        if (!stopByte.HasValue)
        {
            return source;
        }

        var index = Array.IndexOf(source, stopByte.Value);
        return index < 0 ? source : source[..index];
    }

    private static byte[] ApplyByteTrim(byte[] source, byte? trimByte)
    {
        if (!trimByte.HasValue)
        {
            return source;
        }

        var index = 0;
        while (index < source.Length && source[index] == trimByte.Value)
        {
            index++;
        }

        return index == 0 ? source : source[index..];
    }

    private static byte[] ApplyNibbleSkip(byte[] source, string nibbleSkip)
    {
        if (string.IsNullOrWhiteSpace(nibbleSkip))
        {
            return source;
        }

        List<byte> nibbles;
        if (nibbleSkip.Equals("odd", StringComparison.OrdinalIgnoreCase))
        {
            nibbles = source.Select(static b => (byte)(b & 0x0F)).ToList();
        }
        else if (nibbleSkip.Equals("even", StringComparison.OrdinalIgnoreCase))
        {
            nibbles = source.Select(static b => (byte)((b >> 4) & 0x0F)).ToList();
        }
        else
        {
            return source;
        }

        if (nibbles.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var packed = new List<byte>((nibbles.Count + 1) / 2);
        var hasPartialByte = nibbles.Count % 2 != 0;
        for (var i = 0; i < nibbles.Count; i += 2)
        {
            if (i + 1 < nibbles.Count)
            {
                packed.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
            }
            else
            {
                packed.Add((byte)(nibbles[i] << 4));
            }
        }

        if (!hasPartialByte)
        {
            return packed.ToArray();
        }

        var shifted = new byte[packed.Count];
        byte carry = 0;
        for (var i = 0; i < packed.Count; i++)
        {
            var current = packed[i];
            shifted[i] = (byte)((current >> 4) | carry);
            carry = (byte)((current & 0x0F) << 4);
        }

        return shifted;
    }

    private static byte[] ApplyNibbleTrim(byte[] source, int? nibble)
    {
        if (!nibble.HasValue || source.Length == 0)
        {
            return source;
        }

        var chars = string.Concat(source.Select(static b => b.ToString("X2", CultureInfo.InvariantCulture))).ToList();
        var nibbleHex = nibble.Value.ToString("X1", CultureInfo.InvariantCulture)[0];
        while (chars.Count > 0 && chars[0] == nibbleHex)
        {
            chars.RemoveAt(0);
        }

        if (chars.Count == 0)
        {
            return Array.Empty<byte>();
        }

        if (chars.Count % 2 != 0)
        {
            chars.Add(nibbleHex);
        }

        var output = new byte[chars.Count / 2];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = byte.Parse(
                new string(new[] { chars[i * 2], chars[(i * 2) + 1] }),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
        }

        return output;
    }

    private static List<int> SplitSymbols(byte[] source, Hi2TxtElementNode element)
    {
        if (source.Length == 0)
        {
            return new List<int>();
        }

        var bitLength = source.Length * 8;
        var unitBits = Math.Max(element.SrcUnitSize, 1);
        var values = new List<int>(Math.Max(1, bitLength / unitBits));
        var bitCursor = 0;

        while (bitCursor < bitLength)
        {
            var available = Math.Min(unitBits, bitLength - bitCursor);
            var raw = ReadBits(source, bitCursor, available);
            bitCursor += unitBits;

            if (element.NumberBase == 10 || element.NumberBase == 16)
            {
                values.Add(raw);
                continue;
            }

            values.AddRange(SplitFromBase(raw, element.DstUnitSize, element.NumberBase));
        }

        return values;
    }

    private static int ReadBits(byte[] source, int bitOffset, int bitCount)
    {
        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var absoluteBit = bitOffset + i;
            var byteIndex = absoluteBit / 8;
            var bitIndex = 7 - (absoluteBit % 8);
            var bit = (source[byteIndex] >> bitIndex) & 0x01;
            value = (value << 1) | bit;
        }

        return value;
    }

    private static IEnumerable<int> SplitFromBase(int value, int outputCount, int numberBase)
    {
        if (outputCount <= 1)
        {
            yield return value;
            yield break;
        }

        var chars = new int[outputCount];
        var current = value;
        for (var i = outputCount - 1; i >= 0; i--)
        {
            chars[i] = current % numberBase;
            current /= numberBase;
        }

        foreach (var item in chars)
        {
            yield return item;
        }
    }

    private static byte[] ApplyBitmask(byte[] source, Hi2TxtBitmask bitmask)
    {
        if (source.Length == 0 || bitmask.Masks.Count == 0)
        {
            return source;
        }

        if (bitmask.ByteCompletion)
        {
            var output = new List<byte>(bitmask.Masks.Count * source.Length);
            foreach (var mask in bitmask.Masks)
            {
                output.AddRange(BitsToCompletedBytes(ExtractBits(source, mask)));
            }

            return output.ToArray();
        }

        var bits = new List<int>();
        foreach (var mask in bitmask.Masks)
        {
            bits.AddRange(ExtractBits(source, mask));
        }

        return BitsToBytes(bits);
    }

    private static List<int> ExtractBits(byte[] source, string mask)
    {
        var bits = new List<int>();
        var limit = Math.Min(source.Length * 8, mask.Length);
        for (var i = 0; i < limit; i++)
        {
            if (mask[i] == '1')
            {
                bits.Add(ReadBits(source, i, 1));
            }
        }

        return bits;
    }

    private static byte[] BitsToBytes(IReadOnlyList<int> bits)
    {
        if (bits.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var output = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            var byteIndex = i / 8;
            output[byteIndex] = (byte)((output[byteIndex] << 1) | (bits[i] & 0x01));
        }

        return output;
    }

    private static byte[] BitsToCompletedBytes(IReadOnlyList<int> bits)
    {
        if (bits.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var byteCount = (bits.Count + 7) / 8;
        var value = new System.Numerics.BigInteger(0);
        for (var i = 0; i < bits.Count; i++)
        {
            value = (value << 1) | (bits[i] & 0x01);
        }

        var output = new byte[byteCount];
        for (var i = byteCount - 1; i >= 0; i--)
        {
            output[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return output;
    }

    private sealed class Hi2TxtDescriptor
    {
        private readonly Dictionary<string, List<CharsetMapEntry>> _charsets;
        private readonly Dictionary<string, Hi2TxtBitmask> _bitmasks;
        private readonly Dictionary<string, Hi2TxtFormat> _formats;
        private readonly List<Hi2TxtOutput> _outputs;

        private Hi2TxtDescriptor(
            IReadOnlyList<Hi2TxtStructure> structures,
            Dictionary<string, List<CharsetMapEntry>> charsets,
            Dictionary<string, Hi2TxtBitmask> bitmasks,
            Dictionary<string, Hi2TxtFormat> formats,
            List<Hi2TxtOutput> outputs)
        {
            Structures = structures;
            _charsets = charsets;
            _bitmasks = bitmasks;
            _formats = formats;
            _outputs = outputs;
        }

        public IReadOnlyList<Hi2TxtStructure> Structures { get; }

        public static Hi2TxtDescriptor Load(string descriptorPath)
        {
            var resolvedPath = ResolveSameAs(descriptorPath);
            var document = LoadSanitizedDocument(resolvedPath);
            var root = document.Root ?? throw new InvalidOperationException("Invalid hi2txt descriptor.");

            var charsets = new Dictionary<string, List<CharsetMapEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CS_NUMBER"] =
                [
                    new CharsetMapEntry(0x30, "0", false),
                    new CharsetMapEntry(0x31, "1", false),
                    new CharsetMapEntry(0x32, "2", false),
                    new CharsetMapEntry(0x33, "3", false),
                    new CharsetMapEntry(0x34, "4", false),
                    new CharsetMapEntry(0x35, "5", false),
                    new CharsetMapEntry(0x36, "6", false),
                    new CharsetMapEntry(0x37, "7", false),
                    new CharsetMapEntry(0x38, "8", false),
                    new CharsetMapEntry(0x39, "9", false)
                ]
            };
            foreach (var charsetElement in root.Elements("charset").Where(static c => c.Attribute("id") != null))
            {
                var entries = new List<CharsetMapEntry>();
                foreach (var charElement in charsetElement.Elements("char").Where(static x => x.Attribute("src") != null))
                {
                    entries.Add(new CharsetMapEntry(
                        ParseHexOrInt(charElement.Attribute("src")!.Value),
                        charElement.Attribute("dst")?.Value ?? charElement.Value,
                        ParseBool(charElement.Attribute("default")?.Value)));
                }

                charsets[charsetElement.Attribute("id")!.Value] = entries;
            }

            var bitmasks = root.Elements("bitmask")
                .Where(static b => b.Attribute("id") != null)
                .ToDictionary(
                    static b => b.Attribute("id")!.Value,
                    static b => new Hi2TxtBitmask(
                        !string.Equals(b.Attribute("byte-completion")?.Value, "no", StringComparison.OrdinalIgnoreCase),
                        b.Elements("character")
                            .Select(static c => Regex.Replace(c.Attribute("mask")?.Value ?? string.Empty, @"[^01]", string.Empty))
                            .Where(static mask => !string.IsNullOrWhiteSpace(mask))
                            .ToList()),
                    StringComparer.OrdinalIgnoreCase);

            var formatElements = root.Elements("format")
                .Where(static f => f.Attribute("id") != null)
                .ToDictionary(static f => f.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase);

            var formats = new Dictionary<string, Hi2TxtFormat>(StringComparer.OrdinalIgnoreCase);
            foreach (var formatId in formatElements.Keys)
            {
                BuildFormat(formatId, formatElements, formats, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var outputs = root.Elements("output")
                .Select(element => ParseOutput(element, formats, formatElements))
                .ToList();

            var structures = root.Elements("structure")
                .Select(element => ParseStructure(element, formats, formatElements))
                .ToList();

            return new Hi2TxtDescriptor(structures, charsets, bitmasks, formats, outputs);
        }

        public IEnumerable<Hi2TxtStructure> MatchStructures(string fileExtension, int fileSize)
        {
            return Structures.Where(structure =>
                structure.File.Equals(fileExtension, StringComparison.OrdinalIgnoreCase)
                && (structure.ExpectedSizes.Count == 0 || structure.ExpectedSizes.Contains(fileSize)));
        }

        public Hi2TxtOutput? GetOutput(string? outputId)
        {
            if (!string.IsNullOrWhiteSpace(outputId))
            {
                return _outputs.FirstOrDefault(output => string.Equals(output.Id, outputId, StringComparison.OrdinalIgnoreCase));
            }

            return _outputs.FirstOrDefault(output => string.IsNullOrWhiteSpace(output.Id))
                ?? _outputs.FirstOrDefault();
        }

        public Hi2TxtOutputTable CreateFallbackTable()
        {
            return new Hi2TxtOutputTable(string.Empty, new[]
            {
                new Hi2TxtColumn("RANK", "index", Array.Empty<Hi2TxtFormat>()),
                new Hi2TxtColumn("SCORE", null, Array.Empty<Hi2TxtFormat>()),
                new Hi2TxtColumn("NAME", null, Array.Empty<Hi2TxtFormat>())
            }, null, false, Array.Empty<Hi2TxtFormat>(), null, null, "==", -1);
        }

        public bool TryMapCharset(string charsetId, int value, out string mapped)
        {
            if (!_charsets.TryGetValue(charsetId, out var charset) &&
                TryCloneCharset(charsetId, out charset))
            {
                _charsets[charsetId] = charset;
            }

            if (_charsets.TryGetValue(charsetId, out var resolvedCharset))
            {
                foreach (var entry in resolvedCharset)
                {
                    if (entry.Source == value)
                    {
                        mapped = entry.Target;
                        return true;
                    }
                }

                var fallback = resolvedCharset.FirstOrDefault(static entry => entry.IsDefault);
                if (fallback != null)
                {
                    mapped = fallback.Target;
                    return true;
                }
            }

            mapped = string.Empty;
            return false;
        }

        private bool TryCloneCharset(string charsetId, out List<CharsetMapEntry> charset)
        {
            charset = new List<CharsetMapEntry>();

            var match = Regex.Match(charsetId, @"^(.+)\[(-?\d+)(?:,(-?\d+))?\]$");
            if (!match.Success)
            {
                return false;
            }

            var baseId = match.Groups[1].Value;
            if (!_charsets.TryGetValue(baseId, out var baseCharset))
            {
                return false;
            }

            var offset = ParseInt(match.Groups[2].Value, 0);
            var step = match.Groups[3].Success ? ParseInt(match.Groups[3].Value, 0) : 0;
            for (var i = 0; i < baseCharset.Count; i++)
            {
                var entry = baseCharset[i];
                charset.Add(entry with { Source = entry.Source + offset + (i * step) });
            }

            return true;
        }

        public bool TryGetBitmask(string bitmaskId, out Hi2TxtBitmask bitmask)
        {
            if (_bitmasks.TryGetValue(bitmaskId, out var mask))
            {
                bitmask = mask;
                return true;
            }

            bitmask = default!;
            return false;
        }

        public string ApplyFormats(string value, IReadOnlyList<Hi2TxtFormat> formats, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            var current = value;
            foreach (var format in formats)
            {
                current = format.ApplyToCharacters
                    ? ApplyFormatPerCharacter(current, format, dataSeries, rowIndex, orderedIndex)
                    : ApplyFormat(current, format, dataSeries, rowIndex, orderedIndex);
            }

            return current;
        }

        private string ApplyFormatPerCharacter(string current, Hi2TxtFormat format, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            if (string.IsNullOrEmpty(current))
            {
                return ApplyFormat(current, format, dataSeries, rowIndex, orderedIndex);
            }

            var builder = new StringBuilder(current.Length * 2);
            foreach (var ch in current)
            {
                builder.Append(ApplyFormat(ch.ToString(), format, dataSeries, rowIndex, orderedIndex));
            }

            return builder.ToString();
        }

        public string RenderColumn(Hi2TxtColumn column, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            string value;
            var source = column.Source ?? column.Id;
            if (string.Equals(source, "index", StringComparison.OrdinalIgnoreCase))
            {
                value = orderedIndex.ToString(CultureInfo.InvariantCulture);
            }
            else if (string.Equals(source, "unsorted_index", StringComparison.OrdinalIgnoreCase))
            {
                value = rowIndex.ToString(CultureInfo.InvariantCulture);
            }
            else if (TryGetSeriesValue(dataSeries, source, rowIndex, out var sourceValue))
            {
                value = sourceValue;
            }
            else if (TryGetSeriesValue(dataSeries, column.Id, rowIndex, out sourceValue))
            {
                value = sourceValue;
            }
            else
            {
                value = string.Empty;
            }

            return ApplyFormats(value, column.Formats, dataSeries, rowIndex, orderedIndex);
        }

        private string ApplyFormat(string value, Hi2TxtFormat format, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            var current = value;
            foreach (var operation in format.Operations)
            {
                current = operation switch
                {
                    AddOperation add => ApplyNumeric(current, add.Amount, static (a, b) => a + b),
                    SubtractOperation subtract => ApplyNumeric(current, subtract.Amount, static (a, b) => a - b),
                    MultiplyOperation multiply => ApplyNumeric(current, multiply.Amount, static (a, b) => a * b),
                    DivideOperation divide => ApplyDivision(current, divide.Amount, round: false, truncate: false),
                    DivideRoundOperation divideRound => ApplyDivision(current, divideRound.Amount, round: true, truncate: false),
                    DivideTruncateOperation divideTruncate => ApplyDivision(current, divideTruncate.Amount, round: false, truncate: true),
                    RemainderOperation remainder => ApplyNumeric(current, remainder.Amount, static (a, b) => a % b),
                    ShiftOperation shift => ApplyNumeric(current, shift.Amount, static (a, b) => a * Math.Pow(10, b)),
                    PrefixOperation prefix => string.IsNullOrEmpty(current) ? current : prefix.Text + current,
                    SuffixOperation suffix => string.IsNullOrEmpty(current) ? current : current + suffix.Text,
                    TrimOperation trim => ApplyTrim(current, trim),
                    PadOperation pad => ApplyPad(current, pad),
                    UpperOperation => current.ToUpperInvariant(),
                    LowerOperation => current.ToLowerInvariant(),
                    CapitalizeOperation => Capitalize(current),
                    HexadecimalOperation => string.IsNullOrEmpty(current) ? current : "0x" + current,
                    ConcatOperation concat => ApplyConcat(current, concat, format.InputAsSubcolumnsInput, dataSeries, rowIndex, orderedIndex),
                    CaseOperation @case => ApplyCase(current, @case, dataSeries, rowIndex, orderedIndex),
                    ReplaceOperation replace => ApplyReplace(current, replace),
                    TruncOperation => ApplyTrunc(current),
                    RoundOperation => ApplyRound(current),
                    SumOperation sum => ApplyAggregate(current, sum.Fields, format.InputAsSubcolumnsInput, dataSeries, rowIndex, orderedIndex, static (left, right) => left + right),
                    MaxOperation max => ApplyAggregate(current, max.Fields, format.InputAsSubcolumnsInput, dataSeries, rowIndex, orderedIndex, static (left, right) => left >= right ? left : right),
                    _ => current
                };
            }

            if (!string.IsNullOrWhiteSpace(format.Formatter))
            {
                current = ApplyFormatter(current, format.Formatter!);
            }

            return current;
        }

        private string ApplyCase(string current, CaseOperation operation, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            foreach (var entry in operation.Cases)
            {
                var currentValue = ApplyFormats(current, entry.MatchFormats, dataSeries, rowIndex, orderedIndex);
                if (IsCaseMatch(currentValue, entry))
                {
                    var target = entry.HasExplicitTarget ? entry.Target : current;
                    return ApplyFormats(target, entry.ResultFormats, dataSeries, rowIndex, orderedIndex);
                }
            }

            return operation.Default ?? current;
        }

        private string ApplyConcat(string current, ConcatOperation operation, bool inputAsSubcolumnsInput, IReadOnlyDictionary<string, List<string>> dataSeries, int rowIndex, int orderedIndex)
        {
            var builder = new StringBuilder();
            foreach (var part in operation.Parts)
            {
                switch (part)
                {
                    case ConcatTextPart textPart:
                        builder.Append(textPart.Text);
                        break;
                    case ConcatFieldPart fieldPart:
                        var fieldValue = fieldPart.UseCurrentInput
                            ? ApplyFormats(current, fieldPart.Formats, dataSeries, rowIndex, orderedIndex)
                            : RenderColumn(new Hi2TxtColumn(fieldPart.Id, fieldPart.Source, fieldPart.Formats), dataSeries, rowIndex, orderedIndex);
                        builder.Append(fieldValue);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string ApplyPad(string current, PadOperation operation)
        {
            var source = current ?? string.Empty;
            var padding = string.IsNullOrEmpty(operation.Text) ? " " : operation.Text;
            while (source.Length < operation.MaxLength)
            {
                source = operation.Direction == TrimDirection.Left
                    ? padding + source
                    : source + padding;
            }

            return source;
        }

        private static string ApplyReplace(string current, ReplaceOperation operation)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(operation.Source))
            {
                return current;
            }

            return Regex.Replace(current, operation.Source, operation.Target);
        }

        private static string ApplyTrunc(string current)
        {
            if (!TryParseNumeric(current, out var value))
            {
                return current;
            }

            return FormatDecimal(decimal.Truncate(value));
        }

        private static string ApplyRound(string current)
        {
            if (!TryParseNumeric(current, out var value))
            {
                return current;
            }

            return FormatDecimal(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
        }

        private string ApplyAggregate(
            string current,
            IReadOnlyList<OperationField> fields,
            bool inputAsSubcolumnsInput,
            IReadOnlyDictionary<string, List<string>> dataSeries,
            int rowIndex,
            int orderedIndex,
            Func<decimal, decimal, decimal> operation)
        {
            decimal? accumulator = fields.Count == 0 && TryParseNumeric(current, out var currentValue)
                ? currentValue
                : null;

            foreach (var value in EnumerateAggregateValues(current, fields, inputAsSubcolumnsInput, dataSeries, rowIndex, orderedIndex))
            {
                if (!TryParseNumeric(value, out var parsed))
                {
                    continue;
                }

                accumulator = accumulator.HasValue
                    ? operation(accumulator.Value, parsed)
                    : parsed;
            }

            return accumulator.HasValue ? FormatDecimal(accumulator.Value) : current;
        }

        private IEnumerable<string> EnumerateAggregateValues(
            string current,
            IReadOnlyList<OperationField> fields,
            bool inputAsSubcolumnsInput,
            IReadOnlyDictionary<string, List<string>> dataSeries,
            int rowIndex,
            int orderedIndex)
        {
            if (fields.Count == 1 && !fields[0].UseCurrentInput)
            {
                var source = fields[0].Source ?? fields[0].Id;
                if (dataSeries.TryGetValue(source, out var values))
                {
                    for (var i = 0; i < values.Count; i++)
                    {
                        yield return ApplyFormats(values[i], fields[0].Formats, dataSeries, i, i);
                    }

                    yield break;
                }
            }

            foreach (var field in fields)
            {
                if (field.UseCurrentInput)
                {
                    yield return ApplyFormats(current, field.Formats, dataSeries, rowIndex, orderedIndex);
                    continue;
                }

                yield return RenderColumn(new Hi2TxtColumn(field.Id, field.Source, field.Formats), dataSeries, rowIndex, orderedIndex);
            }
        }

        private static bool IsCaseMatch(string current, CaseMapping entry)
        {
            var comparisonOperator = string.IsNullOrWhiteSpace(entry.Operator) ? "==" : entry.Operator;
            if (string.IsNullOrWhiteSpace(entry.Operator) &&
                TryParseHexCharacter(entry.Source, out var expectedChar) &&
                current.Length == 1)
            {
                return current[0] == expectedChar;
            }

            if (TryParseNumeric(current, out var left) && TryParseNumeric(entry.Source, out var right))
            {
                return comparisonOperator switch
                {
                    "<" => left < right,
                    "<=" => left <= right,
                    ">" => left > right,
                    ">=" => left >= right,
                    "!=" => left != right,
                    "=" or "==" => left == right,
                    _ => string.Equals(current, entry.Source, StringComparison.OrdinalIgnoreCase)
                };
            }

            return comparisonOperator switch
            {
                "!=" => !string.Equals(current, entry.Source, StringComparison.OrdinalIgnoreCase),
                "=" or "==" => string.Equals(current, entry.Source, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(current, entry.Source, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static string Capitalize(string current)
        {
            if (string.IsNullOrEmpty(current))
            {
                return current;
            }

            return char.ToUpperInvariant(current[0]) + current[1..].ToLowerInvariant();
        }

        private static string ApplyTrim(string current, TrimOperation trim)
        {
            if (string.IsNullOrEmpty(current))
            {
                return current;
            }

            return trim.Direction switch
            {
                TrimDirection.Left => current.TrimStart(trim.Character),
                TrimDirection.Right => current.TrimEnd(trim.Character),
                _ => current.Trim(trim.Character)
            };
        }

        private static string ApplyDivision(string current, decimal amount, bool round, bool truncate)
        {
            if (!decimal.TryParse(current, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) || amount == 0)
            {
                return current;
            }

            var value = left / amount;
            if (round)
            {
                value = decimal.Round(value, 0, MidpointRounding.AwayFromZero);
            }
            else if (truncate)
            {
                value = decimal.Truncate(value);
            }

            return FormatDecimal(value);
        }

        private static string ApplyNumeric(string current, decimal amount, Func<double, double, double> operation)
        {
            if (!decimal.TryParse(current, NumberStyles.Number, CultureInfo.InvariantCulture, out var left))
            {
                return current;
            }

            var result = operation((double)left, (double)amount);
            return FormatDecimal((decimal)result);
        }

        private static string FormatDecimal(decimal value)
        {
            return decimal.Truncate(value) == value
                ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
        }

        private static string ApplyFormatter(string current, string formatter)
        {
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(formatter) || !TryParseNumeric(current, out var value))
            {
                return current;
            }

            var match = Regex.Match(
                formatter,
                @"^%(?<zero>0)?(?<width>\d+)?(?:\.(?<precision>\d+))?(?<type>[df])(?<suffix>.*)$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return current;
            }

            var width = match.Groups["width"].Success
                ? int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture)
                : 0;
            var precision = match.Groups["precision"].Success
                ? int.Parse(match.Groups["precision"].Value, CultureInfo.InvariantCulture)
                : (string.Equals(match.Groups["type"].Value, "f", StringComparison.OrdinalIgnoreCase) ? 6 : 0);
            var padCharacter = match.Groups["zero"].Success ? '0' : ' ';
            var suffix = match.Groups["suffix"].Value;

            string formatted;
            if (string.Equals(match.Groups["type"].Value, "d", StringComparison.OrdinalIgnoreCase))
            {
                var integer = decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);
                formatted = width > 0 ? integer.PadLeft(width, padCharacter) : integer;
            }
            else
            {
                formatted = value.ToString($"F{precision}", CultureInfo.InvariantCulture);
                if (width > 0)
                {
                    formatted = formatted.PadLeft(width, padCharacter);
                }
            }

            return formatted + suffix;
        }

        private static bool TryParseHexCharacter(string? text, out char value)
        {
            value = '\0';
            if (string.IsNullOrWhiteSpace(text) ||
                !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) ||
                codePoint is < char.MinValue or > char.MaxValue)
            {
                return false;
            }

            value = (char)codePoint;
            return true;
        }

        private static bool TryParseNumeric(string? text, out decimal value)
        {
            var cleaned = Regex.Replace(text ?? string.Empty, @"[^0-9\.\-]", string.Empty);
            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetSeriesValue(IReadOnlyDictionary<string, List<string>> dataSeries, string key, int rowIndex, out string value)
        {
            if (dataSeries.TryGetValue(key, out var list) && rowIndex < list.Count)
            {
                value = list[rowIndex];
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static Hi2TxtStructure ParseStructure(XElement element, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            var expectedSizes = element.Element("check")?
                .Elements("size")
                .Select(size => ParseNullableInt(size.Value))
                .Where(static size => size.HasValue)
                .Select(static size => size!.Value)
                .ToArray()
                ?? Array.Empty<int>();

            return new Hi2TxtStructure(
                element.Attribute("file")?.Value ?? string.Empty,
                expectedSizes,
                ParseInt(element.Attribute("byte-swap")?.Value, 0),
                ParseBool(element.Attribute("bit-swap")?.Value),
                element.Attribute("output")?.Value,
                ParseNodes(element.Elements().Where(static x => x.Name.LocalName is "loop" or "elt").ToList(), formats, formatElements));
        }

        private static IReadOnlyList<Hi2TxtNode> ParseNodes(IReadOnlyList<XElement> elements, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            return elements.Select(element => ParseNode(element, formats, formatElements)).ToList();
        }

        private static Hi2TxtNode ParseNode(XElement element, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            return element.Name.LocalName switch
            {
                "loop" => new Hi2TxtLoopNode(
                    ParseInt(element.Attribute("count")?.Value, 1),
                    ParseInt(element.Attribute("start")?.Value, 0),
                    ParseInt(element.Attribute("skip-first-bytes")?.Value, 0),
                    ParseInt(element.Attribute("skip-last-bytes")?.Value, 0),
                    ParseNodes(element.Elements().Where(static x => x.Name.LocalName is "loop" or "elt").ToList(), formats, formatElements)),
                "elt" => ParseElement(element, formats, formatElements),
                _ => throw new InvalidOperationException($"Unsupported hi2txt node: {element.Name.LocalName}")
            };
        }

        private static Hi2TxtElementNode ParseElement(XElement element, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            var type = (element.Attribute("type")?.Value ?? string.Empty).Trim().ToLowerInvariant();
            var decodingProfile = element.Attribute("decoding-profile")?.Value ?? string.Empty;

            var baseValue = type is "int" or "text" ? 10 : 0;
            var asciiOffset = ParseInt(element.Attribute("ascii-offset")?.Value, 0);
            var asciiStep = ParseInt(element.Attribute("ascii-step")?.Value, 1);
            var srcUnitSize = ParseInt(element.Attribute("src-unit-size")?.Value, 8);
            var dstUnitSize = ParseInt(element.Attribute("dst-unit-size")?.Value, 1);
            var nibbleSkip = element.Attribute("nibble-skip")?.Value ?? string.Empty;
            var littleEndian = string.Equals(element.Attribute("endianness")?.Value, "little_endian", StringComparison.OrdinalIgnoreCase);

            switch (decodingProfile)
            {
                case "base-40":
                    baseValue = 40;
                    asciiOffset = element.Attribute("ascii-offset") == null ? 64 : asciiOffset;
                    srcUnitSize = element.Attribute("src-unit-size") == null ? 16 : srcUnitSize;
                    dstUnitSize = element.Attribute("dst-unit-size") == null ? 3 : dstUnitSize;
                    break;
                case "base-32":
                    baseValue = 32;
                    asciiOffset = element.Attribute("ascii-offset") == null ? 64 : asciiOffset;
                    srcUnitSize = element.Attribute("src-unit-size") == null ? 16 : srcUnitSize;
                    dstUnitSize = element.Attribute("dst-unit-size") == null ? 3 : dstUnitSize;
                    break;
                case "bcd":
                    baseValue = 16;
                    nibbleSkip = "odd";
                    break;
                case "bcd-le":
                    baseValue = 16;
                    nibbleSkip = "odd";
                    littleEndian = true;
                    break;
            }

            baseValue = ParseInt(element.Attribute("base")?.Value, baseValue);

            return new Hi2TxtElementNode(
                element.Attribute("id")?.Value ?? string.Empty,
                type,
                ParseInt(element.Attribute("size")?.Value, 0),
                asciiOffset,
                ResolveCharsetIds(element.Attribute("charset")?.Value),
                decodingProfile,
                ParseNullableHexOrInt(element.Attribute("byte-trunc")?.Value),
                ParseNullableHexOrInt(element.Attribute("byte-trim")?.Value),
                element.Attribute("byte-skip")?.Value ?? string.Empty,
                nibbleSkip,
                ParseNullableNibble(element.Attribute("nibble-trim")?.Value),
                asciiStep,
                baseValue,
                srcUnitSize,
                dstUnitSize,
                element.Attribute("bitmask")?.Value ?? string.Empty,
                littleEndian,
                ParseInt(element.Attribute("byte-swap")?.Value, 0),
                ParseBool(element.Attribute("bit-swap")?.Value),
                element.Attribute("table-index")?.Value ?? string.Empty,
                ResolveFormats(element.Attribute("table-index-format")?.Value, formats, formatElements, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                ResolveFormats(element.Attribute("format")?.Value, formats, formatElements, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }

        private static List<Hi2TxtFormat> ResolveFormats(
            string? formatIds,
            Dictionary<string, Hi2TxtFormat> formats,
            IReadOnlyDictionary<string, XElement> formatElements,
            HashSet<string> building)
        {
            var resolved = new List<Hi2TxtFormat>();
            if (string.IsNullOrWhiteSpace(formatIds))
            {
                return resolved;
            }

            foreach (var rawFormatId in formatIds.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var formatId = rawFormatId;
                var trimmedFormatId = rawFormatId.Trim();

                if (!formats.ContainsKey(formatId) &&
                    !formatElements.ContainsKey(formatId) &&
                    !PreservesTrailingCharacters(formatId) &&
                    !string.Equals(formatId, trimmedFormatId, StringComparison.Ordinal))
                {
                    formatId = trimmedFormatId;
                }

                resolved.Add(BuildFormat(formatId, formatElements, formats, building));
            }

            return resolved;
        }

        private static Hi2TxtFormat BuildFormat(
            string formatId,
            IReadOnlyDictionary<string, XElement> formatElements,
            Dictionary<string, Hi2TxtFormat> formats,
            HashSet<string> building)
        {
            if (formats.TryGetValue(formatId, out var existing))
            {
                return existing;
            }

            if (!formatElements.TryGetValue(formatId, out var formatElement))
            {
                var autoFormat = CreateAutoFormat(formatId);
                formats[formatId] = autoFormat;
                return autoFormat;
            }

            if (!building.Add(formatId))
            {
                return formats.TryGetValue(formatId, out existing) ? existing : CreateAutoFormat(formatId);
            }

            var format = ParseFormat(formatElement, formats, formatElements, building);
            formats[format.Id] = format;
            building.Remove(formatId);
            return format;
        }

        private static IReadOnlyList<string> ResolveCharsetIds(string? charsetIds)
        {
            if (string.IsNullOrWhiteSpace(charsetIds))
            {
                return Array.Empty<string>();
            }

            return charsetIds
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
        }

        private static Hi2TxtOutput ParseOutput(XElement element, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            return new Hi2TxtOutput(
                element.Attribute("id")?.Value,
                element.Elements("table").Select(table => ParseTable(table, formats, formatElements)).ToList());
        }

        private static Hi2TxtOutputTable ParseTable(XElement element, Dictionary<string, Hi2TxtFormat> formats, IReadOnlyDictionary<string, XElement> formatElements)
        {
            var lineIgnore = element.Attribute("line-ignore")?.Value;
            var lineIgnoreSeparator = lineIgnore?.LastIndexOf(':') ?? -1;
            var lineIgnoreColumn = lineIgnoreSeparator >= 0 ? lineIgnore![..lineIgnoreSeparator] : lineIgnore;
            var lineIgnoreValue = lineIgnoreSeparator >= 0 ? lineIgnore![(lineIgnoreSeparator + 1)..] : string.Empty;

            return new Hi2TxtOutputTable(
                element.Attribute("id")?.Value ?? string.Empty,
                element.Elements()
                    .Where(static child => child.Name.LocalName is "column" or "field")
                    .Select(column => new Hi2TxtColumn(
                        column.Attribute("id")?.Value ?? string.Empty,
                        column.Attribute("src")?.Value,
                        ResolveFormats(column.Attribute("format")?.Value, formats, formatElements, new HashSet<string>(StringComparer.OrdinalIgnoreCase))))
                    .ToList(),
                element.Attribute("sort")?.Value,
                string.Equals(element.Attribute("sort-order")?.Value, "desc", StringComparison.OrdinalIgnoreCase),
                ResolveFormats(element.Attribute("sort-format")?.Value, formats, formatElements, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                lineIgnoreColumn,
                lineIgnoreValue,
                element.Attribute("line-ignore-operator")?.Value ?? "==",
                ParseInt(element.Attribute("lines-max")?.Value, -1));
        }

        private static Hi2TxtFormat ParseFormat(
            XElement element,
            Dictionary<string, Hi2TxtFormat> formats,
            IReadOnlyDictionary<string, XElement> formatElements,
            HashSet<string> building)
        {
            var operations = new List<IFormatOperation>();
            var pendingCases = new List<CaseMapping>();
            var inputAsSubcolumnsInput = ParseBool(element.Attribute("input-as-subcolumns-input")?.Value);

            foreach (var child in element.Elements())
            {
                if (!string.Equals(child.Name.LocalName, "case", StringComparison.OrdinalIgnoreCase) &&
                    pendingCases.Count > 0)
                {
                    operations.Add(new CaseOperation(pendingCases.ToArray(), pendingCases.Select(static c => c.Default).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))));
                    pendingCases.Clear();
                }

                switch (child.Name.LocalName)
                {
                    case "add":
                        operations.Add(new AddOperation(ParseDecimal(child.Value, 0)));
                        break;
                    case "substract":
                        operations.Add(new SubtractOperation(ParseDecimal(child.Value, 0)));
                        break;
                    case "multiply":
                        operations.Add(new MultiplyOperation(ParseDecimal(child.Value, 1)));
                        break;
                    case "divide":
                        operations.Add(new DivideOperation(ParseDecimal(child.Value, 1)));
                        break;
                    case "divide_trunc":
                        operations.Add(new DivideTruncateOperation(ParseDecimal(child.Value, 1)));
                        break;
                    case "shift":
                        operations.Add(new ShiftOperation(ParseDecimal(child.Value, 0)));
                        break;
                    case "remainder":
                        operations.Add(new RemainderOperation(ParseDecimal(child.Value, 1)));
                        break;
                    case "suffix":
                    case "postfix":
                        operations.Add(new SuffixOperation(child.Value));
                        break;
                    case "prefix":
                        operations.Add(new PrefixOperation(child.Value));
                        break;
                    case "trim":
                        operations.Add(new TrimOperation(ParseTrimDirection(child.Attribute("direction")?.Value), (child.Value.FirstOrDefault() is var trim && trim != '\0') ? trim : ' '));
                        break;
                    case "pad":
                        operations.Add(new PadOperation(
                            child.Attribute("direction")?.Value?.Equals("left", StringComparison.OrdinalIgnoreCase) == true ? TrimDirection.Left : TrimDirection.Right,
                            ParseInt(child.Attribute("max")?.Value, 0),
                            child.Value));
                        break;
                    case "uppercase":
                        operations.Add(new UpperOperation());
                        break;
                    case "lowercase":
                        operations.Add(new LowerOperation());
                        break;
                    case "capitalize":
                        operations.Add(new CapitalizeOperation());
                        break;
                    case "replace":
                        operations.Add(new ReplaceOperation(
                            child.Attribute("src")?.Value ?? string.Empty,
                            child.Attribute("dst")?.Value ?? child.Value));
                        break;
                    case "trunc":
                        operations.Add(new TruncOperation());
                        break;
                    case "round":
                        operations.Add(new RoundOperation());
                        break;
                    case "sum":
                        operations.Add(new SumOperation(ParseOperationFields(child, formats, formatElements, building).ToList()));
                        break;
                    case "max":
                        operations.Add(new MaxOperation(ParseOperationFields(child, formats, formatElements, building).ToList()));
                        break;
                    case "concat":
                        operations.Add(new ConcatOperation(ParseConcatParts(child, formats, formatElements, building).ToList()));
                        break;
                    case "case":
                        pendingCases.Add(new CaseMapping(
                            child.Attribute("src")?.Value ?? string.Empty,
                            child.Attribute("dst")?.Value ?? child.Value,
                            child.Attribute("dst") != null || !string.IsNullOrWhiteSpace(child.Value),
                            child.Attribute("default")?.Value,
                            child.Attribute("operator")?.Value,
                            ResolveFormats(child.Attribute("operator-format")?.Value, formats, formatElements, building),
                            ResolveFormats(child.Attribute("format")?.Value, formats, formatElements, building)));
                        break;
                }
            }

            if (pendingCases.Count > 0)
            {
                operations.Add(new CaseOperation(pendingCases, pendingCases.Select(static c => c.Default).FirstOrDefault(static d => !string.IsNullOrWhiteSpace(d))));
            }

            return new Hi2TxtFormat(
                element.Attribute("id")?.Value ?? string.Empty,
                operations,
                inputAsSubcolumnsInput,
                string.Equals(element.Attribute("apply-to")?.Value, "char", StringComparison.OrdinalIgnoreCase),
                element.Attribute("formatter")?.Value);
        }

        private static IEnumerable<IConcatPart> ParseConcatParts(
            XElement element,
            Dictionary<string, Hi2TxtFormat> formats,
            IReadOnlyDictionary<string, XElement> formatElements,
            HashSet<string> building)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "column")
                {
                    var hasFieldBinding = child.Attribute("id") != null || child.Attribute("src") != null;
                    if (!hasFieldBinding)
                    {
                        yield return new ConcatFieldPart(
                            string.Empty,
                            null,
                            ResolveFormats(child.Attribute("format")?.Value, formats, formatElements, building),
                            true);
                        continue;
                    }

                    yield return new ConcatFieldPart(
                        child.Attribute("id")?.Value ?? string.Empty,
                        child.Attribute("src")?.Value,
                        ResolveFormats(child.Attribute("format")?.Value, formats, formatElements, building),
                        !hasFieldBinding);
                }
                else if (child.Name.LocalName is "txt" or "field")
                {
                    yield return new ConcatTextPart(child.Value);
                }
            }
        }

        private static IEnumerable<OperationField> ParseOperationFields(
            XElement element,
            Dictionary<string, Hi2TxtFormat> formats,
            IReadOnlyDictionary<string, XElement> formatElements,
            HashSet<string> building)
        {
            foreach (var child in element.Elements().Where(static child => child.Name.LocalName is "column" or "field"))
            {
                var hasFieldBinding = child.Attribute("id") != null || child.Attribute("src") != null;
                yield return new OperationField(
                    child.Attribute("id")?.Value ?? string.Empty,
                    child.Attribute("src")?.Value,
                    ResolveFormats(child.Attribute("format")?.Value, formats, formatElements, building),
                    !hasFieldBinding);
            }
        }

        private static Hi2TxtFormat CreateAutoFormat(string id)
        {
            if (string.Equals(id, "0x", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "hexadecimal_string", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new HexadecimalOperation() }, false, false, null);
            }

            if (id.Equals("UC", StringComparison.OrdinalIgnoreCase) || id.Equals("UpperCase", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new UpperOperation() }, false, false, null);
            }

            if (id.Equals("LC", StringComparison.OrdinalIgnoreCase) || id.Equals("LowerCase", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new LowerOperation() }, false, false, null);
            }

            if (id.Equals("Capitalize", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new CapitalizeOperation() }, false, false, null);
            }

            if (id.StartsWith("TrimL", StringComparison.OrdinalIgnoreCase) && id.Length > 5)
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Left, id[5]) }, false, false, null);
            }
            if (id.Equals("TrimL", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Left, ' ') }, false, false, null);
            }

            if (id.StartsWith("TrimR", StringComparison.OrdinalIgnoreCase) && id.Length > 5)
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Right, id[5]) }, false, false, null);
            }
            if (id.Equals("TrimR", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Right, ' ') }, false, false, null);
            }

            if (id.StartsWith("Trim", StringComparison.OrdinalIgnoreCase) && id.Length > 4)
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Both, id[4]) }, false, false, null);
            }
            if (id.Equals("Trim", StringComparison.OrdinalIgnoreCase))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new TrimOperation(TrimDirection.Both, ' ') }, false, false, null);
            }

            if (id.StartsWith("PadL", StringComparison.OrdinalIgnoreCase) &&
                id.Length > 5 &&
                int.TryParse(id[4..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var padLeftLength))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new PadOperation(TrimDirection.Left, padLeftLength, id[^1].ToString()) }, false, false, null);
            }

            if (id.StartsWith("PadR", StringComparison.OrdinalIgnoreCase) &&
                id.Length > 5 &&
                int.TryParse(id[4..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var padRightLength))
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new PadOperation(TrimDirection.Right, padRightLength, id[^1].ToString()) }, false, false, null);
            }

            if (id.StartsWith("Suffix", StringComparison.OrdinalIgnoreCase) && id.Length > 6)
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new SuffixOperation(id[6..]) }, false, false, null);
            }

            if (id.StartsWith("Prefix", StringComparison.OrdinalIgnoreCase) && id.Length > 6)
            {
                return new Hi2TxtFormat(id, new IFormatOperation[] { new PrefixOperation(id[6..]) }, false, false, null);
            }

            if (id.Length > 1 &&
                decimal.TryParse(id[1..], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                return id[0] switch
                {
                    '+' => new Hi2TxtFormat(id, new IFormatOperation[] { new AddOperation(amount) }, false, false, null),
                    '*' => new Hi2TxtFormat(id, new IFormatOperation[] { new MultiplyOperation(amount) }, false, false, null),
                    '/' => new Hi2TxtFormat(id, new IFormatOperation[] { new DivideOperation(amount) }, false, false, null),
                    'd' => new Hi2TxtFormat(id, new IFormatOperation[] { new DivideTruncateOperation(amount) }, false, false, null),
                    'D' => new Hi2TxtFormat(id, new IFormatOperation[] { new DivideRoundOperation(amount) }, false, false, null),
                    '%' => new Hi2TxtFormat(id, new IFormatOperation[] { new RemainderOperation(amount) }, false, false, null),
                    '>' => new Hi2TxtFormat(id, new IFormatOperation[] { new ShiftOperation(amount) }, false, false, null),
                    '-' => new Hi2TxtFormat(id, new IFormatOperation[] { new SubtractOperation(amount) }, false, false, null),
                    _ => new Hi2TxtFormat(id, Array.Empty<IFormatOperation>(), false, false, null)
                };
            }

            return new Hi2TxtFormat(id, Array.Empty<IFormatOperation>(), false, false, null);
        }

        private static string ResolveSameAs(string descriptorPath)
        {
            var currentPath = descriptorPath;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (File.Exists(currentPath) && visited.Add(currentPath))
            {
                var document = LoadSanitizedDocument(currentPath);
                var sameAs = document.Root?.Element("sameas")?.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(sameAs))
                {
                    return currentPath;
                }

                currentPath = Path.Combine(Path.GetDirectoryName(currentPath)!, sameAs + ".xml");
            }

            return currentPath;
        }

        private static XDocument LoadSanitizedDocument(string path)
        {
            var content = File.ReadAllText(path);
            content = Regex.Replace(
                content,
                @"&(?!(amp|lt|gt|quot|apos);)([A-Za-z0-9\-]+);",
                static match => match.Groups[2].Value.ToLowerInvariant() switch
                {
                    "copyright" => "\u00A9",
                    "asterism" => "\u2042",
                    "black-heart" => "\u2665",
                    "black-diamond" => "\u2666",
                    "black-club" => "\u2663",
                    "black-spade" => "\u2660",
                    "black-right-arrow-large" => "\u27A4",
                    "black-star" => "\u2605",
                    "smiley" => "\u263A",
                    "mid-dot" => "\u00B7",
                    "left-arrow" => "\u2190",
                    "phone" => "\u260E",
                    "baseball" => "\u26BE",
                    "thumbs-up" => "\u221A",
                    "airplane" => "\u2708",
                    "heart-with-arrow" => "\u2665",
                    "h-lines-in-square" => "\u25A4",
                    "slash-in-square" => "\u25A7",
                    "antislash-in-square" => "\u25A8",
                    "womens-symbol" => "\u2640",
                    "mens-symbol" => "\u2642",
                    "crescent-moon" => "\u263E",
                    "headset" => "\u260A",
                    "spaceship" => "\u2646",
                    "cross-of-lorraine" => "\u2628",
                    "single-music-note" => "\u266A",
                    "two-exclamations" => "\u203C",
                    "one-on-two" => "\u00BD",
                    "roman-numeral-1" => "\u2160",
                    "roman-numeral-2" => "\u2161",
                    "roman-numeral-3" => "\u2162",
                    "roman-numeral-4" => "\u2163",
                    "skull" => "\u2620",
                    "scissors" => "\u2704",
                    "umbrella" => "\u2602",
                    "whale" => "x",
                    "left-foot" => "\u2308",
                    "right-foot" => "\u2308",
                    "jp-h-i" => "\u3044",
                    "jp-h-ro" => "\u308D",
                    "jp-h-ha" => "\u306F",
                    "jp-h-ni" => "\u306B",
                    "jp-h-ta" => "\u305F",
                    "jp-h-ma" => "\u307E",
                    "jp-h-so" => "\u305D",
                    "jp-h-to" => "\u3068",
                    "jp-h-he" => "\u3078",
                    "jp-h-ho" => "\u307B",
                    "jp-h-dakuten" => "\u309B",
                    "jp-h-handakuten" => "\u309C",
                    "jp-h-sokuon" => "\u3063",
                    "jp-k-hi" => "\u30D2",
                    "jp-k-mi" => "\u30DF",
                    "jp-k-de" => "\u30C7",
                    "jp-k-si" => "\u30B7",
                    "jp-k-tu" => "\u30C4",
                    "jp-k-yo" => "\u30E8",
                    "jp-k-ri" => "\u30EA",
                    "jp-k-gu" => "\u30B0",
                    "jp-k-na" => "\u30CA",
                    "jp-k-ga" => "\u30AC",
                    "jp-k-tu-small" => "\u30C3",
                    _ => $"[{match.Groups[2].Value}]"
                });

            return XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        }

        private static int ParseInt(string? text, int fallback)
        {
            var normalized = text?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return fallback;
            }

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
            {
                return hexValue;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            if (Regex.IsMatch(normalized, @"\A[0-9A-Fa-f]+\z") &&
                int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hexValue))
            {
                return hexValue;
            }

            return fallback;
        }

        private static int? ParseNullableInt(string? text)
        {
            var normalized = text?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return ParseInt(normalized, int.MinValue) is var value && value != int.MinValue
                ? value
                : null;
        }

        private static decimal ParseDecimal(string? text, decimal fallback)
        {
            return decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static bool ParseBool(string? text)
        {
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseHexOrInt(string text)
        {
            var normalized = text.Trim();
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return int.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static byte? ParseNullableHexOrInt(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return (byte)ParseHexOrInt(text);
        }

        private static int? ParseNullableNibble(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return ParseHexOrInt(text);
        }

        private static TrimDirection ParseTrimDirection(string? text)
        {
            return text?.ToLowerInvariant() switch
            {
                "left" => TrimDirection.Left,
                "both" => TrimDirection.Both,
                _ => TrimDirection.Right
            };
        }
    }

    private sealed class EmptyDataSeries : Dictionary<string, List<string>>
    {
        public static readonly EmptyDataSeries Instance = new();
    }

    private sealed class ParseState
    {
        public int LastAssignedIndex { get; set; }
    }

    private abstract record Hi2TxtNode;

    private sealed record Hi2TxtStructure(
        string File,
        IReadOnlyList<int> ExpectedSizes,
        int ByteSwap,
        bool BitSwap,
        string? OutputId,
        IReadOnlyList<Hi2TxtNode> Nodes);

    private sealed record Hi2TxtLoopNode(int Count, int Start, int SkipFirstBytes, int SkipLastBytes, IReadOnlyList<Hi2TxtNode> Nodes) : Hi2TxtNode;

    private sealed record Hi2TxtBitmask(bool ByteCompletion, IReadOnlyList<string> Masks);

    private sealed record Hi2TxtElementNode(
        string Id,
        string Type,
        int Size,
        int AsciiOffset,
        IReadOnlyList<string> CharsetIds,
        string DecodingProfile,
        byte? ByteTrunc,
        byte? ByteTrim,
        string ByteSkip,
        string NibbleSkip,
        int? NibbleTrim,
        int AsciiStep,
        int NumberBase,
        int SrcUnitSize,
        int DstUnitSize,
        string BitmaskId,
        bool LittleEndian,
        int ByteSwap,
        bool BitSwap,
        string TableIndex,
        IReadOnlyList<Hi2TxtFormat> TableIndexFormats,
        IReadOnlyList<Hi2TxtFormat> Formats) : Hi2TxtNode;

    private sealed record Hi2TxtOutput(string? Id, IReadOnlyList<Hi2TxtOutputTable> Tables)
    {
        public Hi2TxtOutputTable SelectScoreTable()
        {
            return Tables.FirstOrDefault(static table =>
                       table.Columns.Any(static column => string.Equals(column.Id, "SCORE", StringComparison.OrdinalIgnoreCase)) &&
                       table.Columns.Any(static column => string.Equals(column.Id, "NAME", StringComparison.OrdinalIgnoreCase)))
                   ?? Tables.FirstOrDefault()
                   ?? new Hi2TxtOutputTable(string.Empty, Array.Empty<Hi2TxtColumn>(), null, false, Array.Empty<Hi2TxtFormat>(), null, null, "==", -1);
        }
    }

    private sealed record Hi2TxtOutputTable(
        string Id,
        IReadOnlyList<Hi2TxtColumn> Columns,
        string? SortColumn,
        bool SortDescending,
        IReadOnlyList<Hi2TxtFormat> SortFormats,
        string? LineIgnoreColumn,
        string? LineIgnoreValue,
        string LineIgnoreOperator,
        int LinesMax)
    {
        public bool ShouldIgnoreRow(IReadOnlyDictionary<string, string> rendered, int alreadyPrinted)
        {
            if (LinesMax >= 0 && alreadyPrinted >= LinesMax)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(LineIgnoreColumn))
            {
                return false;
            }

            if (!rendered.TryGetValue(LineIgnoreColumn, out var current))
            {
                current = string.Empty;
            }

            var expected = LineIgnoreValue ?? string.Empty;
            if (TryParseNumeric(current, out var left) && TryParseNumeric(expected, out var right))
            {
                return LineIgnoreOperator switch
                {
                    "<" => left < right,
                    "<=" => left <= right,
                    ">" => left > right,
                    ">=" => left >= right,
                    "!=" => left != right,
                    "=" or "==" => left == right,
                    _ => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
                };
            }

            return LineIgnoreOperator switch
            {
                "!=" => !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                "=" or "==" => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    private sealed record Hi2TxtColumn(string Id, string? Source, IReadOnlyList<Hi2TxtFormat> Formats);

    private sealed record Hi2TxtFormat(string Id, IReadOnlyList<IFormatOperation> Operations, bool InputAsSubcolumnsInput, bool ApplyToCharacters, string? Formatter);

    private interface IFormatOperation;

    private sealed record AddOperation(decimal Amount) : IFormatOperation;
    private sealed record SubtractOperation(decimal Amount) : IFormatOperation;
    private sealed record MultiplyOperation(decimal Amount) : IFormatOperation;
    private sealed record DivideOperation(decimal Amount) : IFormatOperation;
    private sealed record DivideRoundOperation(decimal Amount) : IFormatOperation;
    private sealed record DivideTruncateOperation(decimal Amount) : IFormatOperation;
    private sealed record RemainderOperation(decimal Amount) : IFormatOperation;
    private sealed record ShiftOperation(decimal Amount) : IFormatOperation;
    private sealed record PrefixOperation(string Text) : IFormatOperation;
    private sealed record SuffixOperation(string Text) : IFormatOperation;
    private sealed record TrimOperation(TrimDirection Direction, char Character) : IFormatOperation;
    private sealed record PadOperation(TrimDirection Direction, int MaxLength, string Text) : IFormatOperation;
    private sealed record UpperOperation() : IFormatOperation;
    private sealed record LowerOperation() : IFormatOperation;
    private sealed record CapitalizeOperation() : IFormatOperation;
    private sealed record HexadecimalOperation() : IFormatOperation;
    private sealed record ReplaceOperation(string Source, string Target) : IFormatOperation;
    private sealed record TruncOperation() : IFormatOperation;
    private sealed record RoundOperation() : IFormatOperation;
    private sealed record SumOperation(IReadOnlyList<OperationField> Fields) : IFormatOperation;
    private sealed record MaxOperation(IReadOnlyList<OperationField> Fields) : IFormatOperation;
    private sealed record ConcatOperation(IReadOnlyList<IConcatPart> Parts) : IFormatOperation;
    private sealed record CaseOperation(IReadOnlyList<CaseMapping> Cases, string? Default) : IFormatOperation;

    private interface IConcatPart;
    private sealed record ConcatTextPart(string Text) : IConcatPart;
    private sealed record ConcatFieldPart(string Id, string? Source, IReadOnlyList<Hi2TxtFormat> Formats, bool UseCurrentInput) : IConcatPart;
    private sealed record OperationField(string Id, string? Source, IReadOnlyList<Hi2TxtFormat> Formats, bool UseCurrentInput);
    private sealed record CaseMapping(string Source, string Target, bool HasExplicitTarget, string? Default, string? Operator, IReadOnlyList<Hi2TxtFormat> MatchFormats, IReadOnlyList<Hi2TxtFormat> ResultFormats);
    private sealed record CharsetMapEntry(int Source, string Target, bool IsDefault);

    private enum TrimDirection
    {
        Left,
        Right,
        Both
    }
}
