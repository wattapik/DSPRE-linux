using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DSPRE.Core;

/// <summary>A model's joint animation (an NSBCA, "JNT0" block): for each joint (an object of the model) how
/// its translation, rotation and scale change over the frames. Read as apicula reads them; a part of the
/// pose a joint's data leaves out is left as the model has it.</summary>
public sealed class JointAnimation
{
    /// <summary>A value over time: nothing, one value for ever, or samples spread over a span of frames.</summary>
    public sealed class Curve<T>
    {
        public static readonly Curve<T> None = new() { Kind = CurveKind.None };
        public CurveKind Kind { get; init; }
        public T Constant { get; init; } = default!;
        public int Start { get; init; }
        public int End { get; init; }
        public T[] Values { get; init; } = [];
    }

    public enum CurveKind { None, Constant, Samples }

    /// <summary>The curves of one joint. Rotations are 3x3 matrices as the files store them (not always true
    /// rotations), kept in a Matrix4x4 with the usual row-vector layout.</summary>
    public sealed class JointCurves
    {
        public required int Index { get; init; }
        public Curve<float>[] Translation { get; init; } = [Curve<float>.None, Curve<float>.None, Curve<float>.None];
        public Curve<Matrix4x4> Rotation { get; set; } = Curve<Matrix4x4>.None;
        public Curve<float>[] Scale { get; init; } = [Curve<float>.None, Curve<float>.None, Curve<float>.None];
    }

    public sealed record Pose(Vector3? Translation, Matrix4x4? Rotation, Vector3? Scale);

    public required string Name { get; init; }
    public required int FrameCount { get; init; }
    public required IReadOnlyList<JointCurves> Joints { get; init; }

    public static JointAnimation Parse(byte[] data)
    {
        if (data.Length < 0x18 || Encoding.ASCII.GetString(data, 0, 4) != "BCA0") throw new InvalidDataException("Not an NSBCA (BCA0) file.");
        int jnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        if (jnt + 8 > data.Length || Encoding.ASCII.GetString(data, jnt, 4) != "JNT0") throw new InvalidDataException("The NSBCA file has no JNT0 block.");

        // The block's info block lists its animations (offsets from the block start) and their names.
        int count = data[jnt + 9];
        int records = jnt + 8 + 4 + 8 + 4 * count + 4;
        if (count == 0) throw new InvalidDataException("The NSBCA file holds no animation.");
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(records));
        string name = Encoding.ASCII.GetString(data, records + 4 * count, 16).TrimEnd('\0', ' ');
        int animation = jnt + offset;

        if (animation + 0x14 > data.Length || !data.AsSpan(animation, 4).SequenceEqual("J\0AC"u8)) throw new InvalidDataException("The NSBCA animation has an unknown layout.");
        int frames = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(animation + 4));
        int objects = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(animation + 6));
        int pivotData = animation + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(animation + 12));
        int basisData = animation + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(animation + 16));
        if (frames == 0) throw new InvalidDataException("The NSBCA animation has no frames.");

        var joints = new List<JointCurves>();
        for (int i = 0; i < objects; i++)
        {
            int at = animation + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(animation + 20 + 2 * i));
            joints.Add(ReadJoint(data, animation, at, pivotData, basisData));
        }

        return new JointAnimation { Name = name, FrameCount = frames, Joints = joints };
    }

    private static uint Bits(uint value, int from, int to) => (value >> from) & ((1u << (to - from)) - 1);

    private static float Fixed(uint raw, int integerBits, int fractionBits)
    {
        int total = 1 + integerBits + fractionBits;
        int value = (int)(raw & (total >= 32 ? uint.MaxValue : (1u << total) - 1));
        if (total < 32 && (value & (1 << (total - 1))) != 0) value -= 1 << total;
        return value / (float)(1 << fractionBits);
    }

    private static JointCurves ReadJoint(byte[] data, int animation, int at, int pivotData, int basisData)
    {
        uint flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        var joint = new JointCurves { Index = data[at + 3] };
        int cursor = at + 4;
        uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor)); cursor += 4; return v; }
        ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor)); cursor += 2; return v; }

        if (Bits(flags, 0, 1) != 0) return joint;

        (int Start, int End, int Width, int Rate) Info(uint raw) => ((int)Bits(raw, 0, 16), (int)Bits(raw, 16, 28), (int)Bits(raw, 28, 30), (int)Bits(raw, 30, 32));

        // A translation or scale component: constant, or samples of 32 bits (1.19.12) or 16 (1.3.12), the
        // scale ones paired with a second value that is not used.
        Curve<float> Component(bool constant, int stride)
        {
            if (constant)
            {
                float value = Fixed(U32(), 19, 12);
                if (stride == 2) U32();
                return new Curve<float> { Kind = CurveKind.Constant, Constant = value };
            }

            (int start, int end, int width, int rate) = Info(U32());
            int where = animation + (int)U32();
            int samples = (end - start) >> rate;
            var values = new float[samples];
            for (int s = 0; s < samples; s++)
            {
                int size = width == 0 ? 4 : 2;
                int position = where + s * size * stride;
                values[s] = width == 0 ? Fixed(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)), 19, 12) : Fixed(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position)), 3, 12);
            }

            return new Curve<float> { Kind = CurveKind.Samples, Start = start, End = end, Values = values };
        }

        if (Bits(flags, 1, 3) == 0)
        {
            for (int axis = 0; axis < 3; axis++) joint.Translation[axis] = Component(Bits(flags, 3 + axis, 4 + axis) != 0, 1);
        }

        if (Bits(flags, 6, 8) == 0)
        {
            Matrix4x4 Fetch(ushort reference)
            {
                int index = reference & 0x7FFF;
                if ((reference & 0x8000) != 0)
                {
                    int p = pivotData + index * 6;
                    ushort selectNegate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
                    float a = Fixed(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2)), 3, 12);
                    float b = Fixed(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4)), 3, 12);
                    return PivotMatrix(selectNegate & 0xF, (selectNegate >> 4) & 0xF, a, b);
                }

                int q = basisData + index * 10;
                var five = new ushort[5];
                for (int k = 0; k < 5; k++) five[k] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(q + 2 * k));
                return BasisMatrix(five);
            }

            if (Bits(flags, 8, 9) != 0)
            {
                ushort reference = U16();
                U16();
                joint.Rotation = new Curve<Matrix4x4> { Kind = CurveKind.Constant, Constant = Fetch(reference) };
            }
            else
            {
                (int start, int end, _, int rate) = Info(U32());
                int where = animation + (int)U32();
                int samples = (end - start) >> rate;
                var values = new Matrix4x4[samples];
                for (int s = 0; s < samples; s++) values[s] = Fetch(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(where + 2 * s)));
                joint.Rotation = new Curve<Matrix4x4> { Kind = CurveKind.Samples, Start = start, End = end, Values = values };
            }
        }

        if (Bits(flags, 9, 11) == 0)
        {
            for (int axis = 0; axis < 3; axis++) joint.Scale[axis] = Component(Bits(flags, 11 + axis, 12 + axis) != 0, 2);
        }

        return joint;
    }

    // The rotation matrices, as the arguments of apicula's pivot_mat (whose Matrix3::new takes columns): a column
    // vector matrix whose columns are given in order is, as a row-vector matrix, the same numbers by rows.
    private static Matrix4x4 FromRows(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22) =>
        new(m00, m01, m02, 0, m10, m11, m12, 0, m20, m21, m22, 0, 0, 0, 0, 1);

    private static Matrix4x4 PivotMatrix(int select, int negate, float a, float b)
    {
        if (select >= 9) return FromRows(-a, 0, 0, 0, 0, 0, 0, 0, 0);
        float o = (negate & 1) == 0 ? 1 : -1;
        float c = (negate & 2) == 0 ? b : -b;
        float d = (negate & 4) == 0 ? a : -a;
        return select switch
        {
            0 => FromRows(o, 0, 0, 0, a, b, 0, c, d),
            1 => FromRows(0, o, 0, a, 0, b, c, 0, d),
            2 => FromRows(0, 0, o, a, b, 0, c, d, 0),
            3 => FromRows(0, a, b, o, 0, 0, 0, c, d),
            4 => FromRows(a, 0, b, 0, o, 0, c, 0, d),
            5 => FromRows(a, b, 0, 0, 0, o, c, d, 0),
            6 => FromRows(0, a, b, 0, c, d, o, 0, 0),
            7 => FromRows(a, 0, b, c, 0, d, 0, o, 0),
            _ => FromRows(a, b, 0, c, d, 0, 0, 0, o)
        };
    }

    // A rotation given by two of its three basis vectors, packed into five 16-bit words (see apicula's basis_mat).
    private static Matrix4x4 BasisMatrix(ushort[] data)
    {
        ushort[] input = [data[4], data[0], data[1], data[2], data[3]];
        var output = new int[6];
        for (int i = 0; i < 5; i++)
        {
            output[i] = input[i] >> 3;
            output[5] = (output[5] << 3) | (input[i] & 7);
        }

        float F(int raw) => Fixed((uint)raw, 0, 12);
        var a = new Vector3(F(output[1]), F(output[2]), F(output[3]));
        var b = new Vector3(F(output[4]), F(output[0]), F(output[5]));
        Vector3 c = Vector3.Cross(a, b);
        return FromRows(a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z);
    }

    private static float Sample(Curve<float> curve, float fallback, int frame)
    {
        switch (curve.Kind)
        {
            case CurveKind.Constant: return curve.Constant;
            case CurveKind.Samples when curve.Values.Length > 0:
                return Interpolate(curve, frame, (x, y, t) => x * (1 - t) + y * t);
            default: return fallback;
        }
    }

    private static T Interpolate<T>(Curve<T> curve, int frame, Func<T, T, float, T> blend)
    {
        if (frame <= curve.Start) return curve.Values[0];
        if (frame >= curve.End - 1) return curve.Values[^1];
        double position = (frame - curve.Start) / (double)(curve.End - 1 - curve.Start) * (curve.Values.Length - 1);
        int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position);
        return blend(curve.Values[low], curve.Values[high], (float)(position - low));
    }

    /// <summary>What the joint's data says about its pose at the frame; a part it has no data for is null.</summary>
    public Pose PoseOf(JointCurves joint, int frame)
    {
        Vector3? translation = joint.Translation.All(c => c.Kind == CurveKind.None) ? null
            : new Vector3(Sample(joint.Translation[0], 0, frame), Sample(joint.Translation[1], 0, frame), Sample(joint.Translation[2], 0, frame));
        Vector3? scale = joint.Scale.All(c => c.Kind == CurveKind.None) ? null
            : new Vector3(Sample(joint.Scale[0], 1, frame), Sample(joint.Scale[1], 1, frame), Sample(joint.Scale[2], 1, frame));
        Matrix4x4? rotation = joint.Rotation.Kind switch
        {
            CurveKind.Constant => joint.Rotation.Constant,
            CurveKind.Samples when joint.Rotation.Values.Length > 0 => Interpolate(joint.Rotation, frame, (x, y, t) => x * (1 - t) + y * t),
            _ => null
        };
        return new Pose(translation, rotation, scale);
    }
}
