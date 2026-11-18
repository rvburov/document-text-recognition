using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;

namespace DocumentTextRecognition.Core.Matching;

/// <summary>Экспериментальный точный Hamming256: четыре кандидата в четырех 64-битных
/// дорожках. Не уменьшает число признаков и не меняет порядок одинаково близких соседей.</summary>
internal static class HammingAvx2
{
    internal sealed class TrainIndex
    {
        internal Mat Source { get; }
        internal int Rows { get; }
        internal FourDescriptors[] Blocks { get; }

        /// <summary>Размер только переставленных данных, без заголовков управляемых объектов.</summary>
        internal long EstimatedBytes => (long)Blocks.Length * 128;

        internal TrainIndex(Mat source, int rows, FourDescriptors[] blocks)
        {
            Source = source;
            Rows = rows;
            Blocks = blocks;
        }
    }

    internal readonly record struct FourDescriptors(Vector256<ulong> Word0, Vector256<ulong> Word1,
        Vector256<ulong> Word2, Vector256<ulong> Word3);

    internal static bool CanUse(Mat descriptors) => Avx2.IsSupported && !descriptors.Empty() &&
        descriptors.Type() == MatType.CV_8UC1 && descriptors.Cols == 32;

    /// <summary>Снимок неизменяемых дескрипторов шаблона. Указатели и native-буферы не хранятся.</summary>
    internal static unsafe TrainIndex? Prepare(Mat descriptors)
    {
        if (!CanUse(descriptors)) return null;
        var rows = descriptors.Rows;
        var step = descriptors.Step();
        var data = (byte*)descriptors.Data;
        var blocks = new FourDescriptors[(rows + 3) / 4];
        for (var block = 0; block < blocks.Length; block++)
        {
            var row = block * 4;
            blocks[block] = new FourDescriptors(
                Word(data, step, rows, row, 0), Word(data, step, rows, row, 8),
                Word(data, step, rows, row, 16), Word(data, step, rows, row, 24));
        }
        GC.KeepAlive(descriptors);
        return new TrainIndex(descriptors, rows, blocks);
    }

    private static unsafe Vector256<ulong> Word(byte* data, long step, int rows, int row, int offset) =>
        Vector256.Create(Read(data, step, rows, row, offset), Read(data, step, rows, row + 1, offset),
            Read(data, step, rows, row + 2, offset), Read(data, step, rows, row + 3, offset));

    private static unsafe ulong Read(byte* data, long step, int rows, int row, int offset) =>
        row < rows ? Unsafe.ReadUnaligned<ulong>(data + row * step + offset) : 0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static unsafe TemplateMatcher.NearestDescriptor[] Find(Mat query, TrainIndex train)
    {
        var rows = query.Rows;
        var step = query.Step();
        var data = (byte*)query.Data;
        var result = new TemplateMatcher.NearestDescriptor[rows];
        var blocks = train.Blocks;
        var fullBlocks = train.Rows / 4;
        var tail = train.Rows % 4;
        var lookup128 = Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);
        var lookup = Vector256.Create(lookup128, lookup128);
        var nibbleMask = Vector256.Create((byte)15);
        for (var row = 0; row < rows; row++)
        {
            var q = data + row * step;
            var q0 = Vector256.Create(Unsafe.ReadUnaligned<ulong>(q));
            var q1 = Vector256.Create(Unsafe.ReadUnaligned<ulong>(q + 8));
            var q2 = Vector256.Create(Unsafe.ReadUnaligned<ulong>(q + 16));
            var q3 = Vector256.Create(Unsafe.ReadUnaligned<ulong>(q + 24));
            int first = -1, second = -1, firstDistance = int.MaxValue, secondDistance = int.MaxValue;
            for (var block = 0; block < fullBlocks; block++)
            {
                var distance = Distances(in blocks[block], q0, q1, q2, q3, lookup, nibbleMask);
                var candidate = block * 4;
                Offer((int)distance.GetElement(0), candidate, ref first, ref second, ref firstDistance, ref secondDistance);
                Offer((int)distance.GetElement(1), candidate + 1, ref first, ref second, ref firstDistance, ref secondDistance);
                Offer((int)distance.GetElement(2), candidate + 2, ref first, ref second, ref firstDistance, ref secondDistance);
                Offer((int)distance.GetElement(3), candidate + 3, ref first, ref second, ref firstDistance, ref secondDistance);
            }
            if (tail != 0)
            {
                var distance = Distances(in blocks[fullBlocks], q0, q1, q2, q3, lookup, nibbleMask);
                var candidate = fullBlocks * 4;
                Offer((int)distance.GetElement(0), candidate, ref first, ref second, ref firstDistance, ref secondDistance);
                if (tail > 1) Offer((int)distance.GetElement(1), candidate + 1, ref first, ref second, ref firstDistance, ref secondDistance);
                if (tail > 2) Offer((int)distance.GetElement(2), candidate + 2, ref first, ref second, ref firstDistance, ref secondDistance);
            }
            result[row] = new TemplateMatcher.NearestDescriptor(first, firstDistance, second, secondDistance);
        }
        GC.KeepAlive(query);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Distances(in FourDescriptors block, Vector256<ulong> q0,
        Vector256<ulong> q1, Vector256<ulong> q2, Vector256<ulong> q3,
        Vector256<byte> lookup, Vector256<byte> mask)
    {
        var counts = Avx2.Add(CountBytes(Avx2.Xor(q0, block.Word0).AsByte(), lookup, mask),
            CountBytes(Avx2.Xor(q1, block.Word1).AsByte(), lookup, mask));
        counts = Avx2.Add(counts, CountBytes(Avx2.Xor(q2, block.Word2).AsByte(), lookup, mask));
        counts = Avx2.Add(counts, CountBytes(Avx2.Xor(q3, block.Word3).AsByte(), lookup, mask));
        // Each byte sums four words (0..32, without overflow); SAD then sums the eight
        // byte positions into one exact 0..256 distance for each candidate lane.
        return Avx2.SumAbsoluteDifferences(counts, Vector256<byte>.Zero).AsUInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> CountBytes(Vector256<byte> value, Vector256<byte> lookup, Vector256<byte> mask)
    {
        var low = Avx2.And(value, mask);
        var high = Avx2.And(Avx2.ShiftRightLogical(value.AsUInt16(), 4).AsByte(), mask);
        return Avx2.Add(Avx2.Shuffle(lookup, low), Avx2.Shuffle(lookup, high));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Offer(int distance, int index, ref int first, ref int second,
        ref int firstDistance, ref int secondDistance)
    {
        // Candidates arrive strictly in original train-row order, so '<' preserves
        // BFMatcher's first/second indices even when multiple distances are equal.
        if (distance < firstDistance)
        {
            secondDistance = firstDistance;
            second = first;
            firstDistance = distance;
            first = index;
        }
        else if (distance < secondDistance)
        {
            secondDistance = distance;
            second = index;
        }
    }
}
