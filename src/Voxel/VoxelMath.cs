namespace Acreage.Voxel;

public static class VoxelMath
{
    public static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        var remainder = value % divisor;
        if (remainder != 0 && ((value < 0) ^ (divisor < 0)))
        {
            result--;
        }

        return result;
    }

    public static int PositiveMod(int value, int modulus)
    {
        var m = value % modulus;
        return m < 0 ? m + modulus : m;
    }
}
