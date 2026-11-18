using System.Runtime.InteropServices;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Matching;

/// <summary>Экспериментальный SIMD-поиск сохраняет обоих точных соседей, исходный порядок
/// при равных расстояниях и поведение скалярного пути на CPU без AVX2.</summary>
public sealed class HammingAvx2Tests
{
    public static IEnumerable<object[]> TailCases =>
        from trainCount in new[] { 1, 2, 3, 4, 5, 7, 8, 9 }
        from queryCount in new[] { 0, 1, 17 }
        select new object[] { queryCount, trainCount };

    [OcrTheory]
    [MemberData(nameof(TailCases))]
    public void Exact_neighbors_match_scalar_and_bf_for_every_partial_four_candidate_block(int queryCount, int trainCount)
    {
        using var query = RandomDescriptors(queryCount, 32, 9123 + queryCount);
        using var train = RandomDescriptors(trainCount, 32, 1847 + trainCount);
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);
        if (TemplateMatcher.HammingAvx2Available) Assert.NotNull(prepared);

        AssertExact(query, train);
        AssertExact(query, train, prepared);
    }

    [OcrTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Padded_lanes_cannot_beat_real_candidates_at_maximum_hamming_distance(int trainCount)
    {
        using var query = new Mat(3, 32, MatType.CV_8UC1, Scalar.All(0));
        using var train = new Mat(trainCount, 32, MatType.CV_8UC1, Scalar.All(255));
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);

        var result = TemplateMatcher.NearestTwoAvx2(query, train, prepared);

        Assert.All(result, p => Assert.Equal(new TemplateMatcher.NearestDescriptor(
            0, 256, trainCount > 1 ? 1 : -1, trainCount > 1 ? 256 : int.MaxValue), p));
        AssertExact(query, train);
        AssertExact(query, train, prepared);
    }

    [OcrFact]
    public void Zero_distance_ties_keep_first_two_original_rows()
    {
        using var query = new Mat(7, 32, MatType.CV_8UC1, Scalar.All(0));
        using var train = new Mat(9, 32, MatType.CV_8UC1, Scalar.All(0));
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);

        Assert.All(TemplateMatcher.NearestTwoAvx2(query, train, prepared), p =>
            Assert.Equal(new TemplateMatcher.NearestDescriptor(0, 0, 1, 0), p));
        AssertExact(query, train);
        AssertExact(query, train, prepared);
    }

    [OcrFact]
    public void Equal_distances_across_blocks_use_original_indices_instead_of_group_order()
    {
        using var query = new Mat(2, 32, MatType.CV_8UC1, Scalar.All(0));
        using var train = new Mat(9, 32, MatType.CV_8UC1, Scalar.All(255));
        var a = new byte[32];
        var b = new byte[32];
        b[0] = 3;
        WriteRow(train, 0, a);
        WriteRow(train, 5, a);
        WriteRow(train, 1, b);
        WriteRow(train, 2, b);
        query.Set(0, 0, (byte)1);
        query.Set(1, 0, (byte)1);
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);

        Assert.All(TemplateMatcher.NearestTwoAvx2(query, train, prepared), p =>
            Assert.Equal(new TemplateMatcher.NearestDescriptor(0, 1, 1, 1), p));
        AssertExact(query, train);
        AssertExact(query, train, prepared);
    }

    [OcrFact]
    public void Unaligned_strided_rows_preserve_random_and_repeated_descriptors()
    {
        using var queryStorage = RandomDescriptors(97, 43, 1211);
        using var trainStorage = RandomDescriptors(83, 47, 3347);
        using var query = new Mat(queryStorage, new Rect(3, 0, 32, queryStorage.Rows));
        using var train = new Mat(trainStorage, new Rect(5, 0, 32, trainStorage.Rows));
        var repeated = ReadRow(train, 0);
        foreach (var row in new[] { 5, 31, 82 }) WriteRow(train, row, repeated);
        foreach (var row in new[] { 0, 1, 17, 96 }) WriteRow(query, row, repeated);
        var queryBefore = ReadRows(query);
        var trainBefore = ReadRows(train);
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);
        if (TemplateMatcher.HammingAvx2Available) Assert.NotNull(prepared);

        AssertExact(query, train);
        AssertExact(query, train, prepared);

        Assert.Equal(queryBefore, ReadRows(query));
        Assert.Equal(trainBefore, ReadRows(train));
    }

    [OcrFact]
    public void Prepared_training_snapshot_is_reused_for_different_query_batches()
    {
        using var train = RandomDescriptors(83, 32, 8093);
        var before = ReadRows(train);
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);
        if (TemplateMatcher.HammingAvx2Available) Assert.NotNull(prepared);
        foreach (var queryCount in new[] { 1, 17, 97, 1 })
        {
            using var query = RandomDescriptors(queryCount, 32, queryCount * 73);
            AssertExact(query, train, prepared);
        }
        Assert.Equal(before, ReadRows(train));
    }

    [OcrFact]
    public void Prepared_index_rejects_another_mat_even_when_data_or_storage_match()
    {
        using var train = RandomDescriptors(9, 32, 5849);
        using var query = RandomDescriptors(3, 32, 7831);
        using var clone = train.Clone();
        using var alias = new Mat(train, new Rect(0, 0, train.Cols, train.Rows));
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);
        if (prepared is null)
        {
            Assert.False(TemplateMatcher.HammingAvx2Available);
            AssertExact(query, clone);
            return;
        }

        Assert.Throws<ArgumentException>(() => TemplateMatcher.NearestTwoAvx2(query, clone, prepared));
        Assert.Throws<ArgumentException>(() => TemplateMatcher.NearestTwoAvx2(query, alias, prepared));
        AssertExact(query, train, prepared);
    }

    [OcrTheory]
    [InlineData(16)]
    [InlineData(64)]
    public void Other_descriptor_widths_preserve_scalar_fallback(int width)
    {
        using var queryStorage = RandomDescriptors(17, width + 9, 1721);
        using var trainStorage = RandomDescriptors(9, width + 13, 8819);
        using var query = new Mat(queryStorage, new Rect(3, 0, width, queryStorage.Rows));
        using var train = new Mat(trainStorage, new Rect(5, 0, width, trainStorage.Rows));

        Assert.Null(TemplateMatcher.PrepareHammingAvx2Train(train));
        AssertExact(query, train);
    }

    [OcrFact]
    public void Empty_training_set_has_missing_neighbors_and_empty_query_has_no_rows()
    {
        using var empty = new Mat();
        using var query = RandomDescriptors(5, 32, 5279);
        using var train = RandomDescriptors(7, 32, 2731);
        var prepared = TemplateMatcher.PrepareHammingAvx2Train(train);

        Assert.Null(TemplateMatcher.PrepareHammingAvx2Train(empty));
        Assert.Empty(TemplateMatcher.NearestTwoAvx2(empty, train, prepared));
        Assert.All(TemplateMatcher.NearestTwoAvx2(query, empty), p => Assert.Equal(
            new TemplateMatcher.NearestDescriptor(-1, int.MaxValue, -1, int.MaxValue), p));
        AssertExact(empty, train, prepared);
        AssertExact(query, empty);
    }

    [OcrFact]
    public void Experimental_selector_preserves_results_and_restores_its_previous_value()
    {
        using var query = RandomDescriptors(97, 32, 7487);
        using var train = RandomDescriptors(83, 32, 3911);
        var expected = TemplateMatcher.NearestTwoScalar(query, train);
        var previous = TemplateMatcher.UseAvx2;
        try
        {
            TemplateMatcher.UseAvx2 = false;
            Assert.Equal<TemplateMatcher.NearestDescriptor>(expected, TemplateMatcher.NearestTwo(query, train));
            TemplateMatcher.UseAvx2 = true;
            Assert.Equal<TemplateMatcher.NearestDescriptor>(expected, TemplateMatcher.NearestTwo(query, train));
        }
        finally { TemplateMatcher.UseAvx2 = previous; }
    }

    private static void AssertExact(Mat query, Mat train, HammingAvx2.TrainIndex? prepared = null)
    {
        var scalar = TemplateMatcher.NearestTwoScalar(query, train);
        var actual = TemplateMatcher.NearestTwoAvx2(query, train, prepared);
        Assert.Equal<TemplateMatcher.NearestDescriptor>(scalar, actual);
        if (query.Empty())
        {
            Assert.Empty(actual);
            return;
        }
        if (train.Empty())
        {
            Assert.All(actual, p => Assert.Equal(new TemplateMatcher.NearestDescriptor(-1, int.MaxValue, -1, int.MaxValue), p));
            return;
        }

        using var bf = new BFMatcher(NormTypes.Hamming);
        var reference = bf.KnnMatch(query, train, 2);
        Assert.Equal(reference.Length, actual.Length);
        for (var row = 0; row < reference.Length; row++)
        {
            Assert.Equal(Math.Min(2, train.Rows), reference[row].Length);
            Assert.Equal(reference[row][0].TrainIdx, actual[row].FirstIndex);
            Assert.Equal((int)reference[row][0].Distance, actual[row].FirstDistance);
            Assert.Equal(reference[row].Length > 1 ? reference[row][1].TrainIdx : -1, actual[row].SecondIndex);
            Assert.Equal(reference[row].Length > 1 ? (int)reference[row][1].Distance : int.MaxValue, actual[row].SecondDistance);
        }
    }

    private static Mat RandomDescriptors(int rows, int width, int seed)
    {
        if (rows == 0) return new Mat();
        var result = new Mat(rows, width, MatType.CV_8UC1);
        var bytes = new byte[rows * width];
        new Random(seed).NextBytes(bytes);
        Marshal.Copy(bytes, 0, result.Data, bytes.Length);
        return result;
    }

    private static byte[] ReadRow(Mat image, int row)
    {
        var bytes = new byte[image.Cols];
        Marshal.Copy(IntPtr.Add(image.Data, checked((int)(row * image.Step()))), bytes, 0, bytes.Length);
        return bytes;
    }

    private static void WriteRow(Mat image, int row, byte[] bytes)
    {
        Assert.Equal(image.Cols, bytes.Length);
        Marshal.Copy(bytes, 0, IntPtr.Add(image.Data, checked((int)(row * image.Step()))), bytes.Length);
    }

    private static byte[] ReadRows(Mat image) => Enumerable.Range(0, image.Rows).SelectMany(row => ReadRow(image, row)).ToArray();
}
