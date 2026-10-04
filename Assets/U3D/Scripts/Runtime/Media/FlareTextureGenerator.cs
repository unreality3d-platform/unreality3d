using UnityEngine;

namespace U3D
{
    public static class FlareTextureGenerator
    {
        public static Texture2D GenerateCircle(int resolution, float gradient, float falloff, bool invert)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[resolution * resolution];
            float halfRes = resolution * 0.5f;
            // Draw shape at 70% of texture space so falloff has room to fade smoothly
            float shapeScale = 1f / 0.7f;

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = (x + 0.5f - halfRes) / halfRes;
                    float dy = (y + 0.5f - halfRes) / halfRes;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) * shapeScale;

                    float alpha = ComputeGradientFalloff(dist, gradient, falloff);
                    if (invert) alpha = 1f - alpha;

                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        public static Texture2D GenerateRing(
            int resolution, float gradient, float falloff, bool invert,
            float ringThickness, float noiseAmplitude, float noiseRepeat, float noiseSpeed)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[resolution * resolution];
            float halfRes = resolution * 0.5f;
            float shapeScale = 0.7f;

            float innerRadius = (1f - ringThickness) * shapeScale;
            float outerRadius = 1f * shapeScale;
            float scaledThickness = outerRadius - innerRadius;

            // Precompute per-angle radial offsets for seamless noise
            int angleSamples = resolution * 2;
            float[] radialOffsets = new float[angleSamples];

            if (noiseAmplitude > 0f)
            {
                float sampleRadius = Mathf.Max(noiseRepeat * 8f, 1f) * 0.5f;

                for (int a = 0; a < angleSamples; a++)
                {
                    float angle = (a / (float)angleSamples) * 2f * Mathf.PI;
                    float nx = Mathf.Cos(angle) * sampleRadius + 100f;
                    float ny = Mathf.Sin(angle) * sampleRadius + 100f;
                    float n = Mathf.PerlinNoise(nx, ny) * 0.6f
                        + Mathf.PerlinNoise(nx * 2.13f + 50f, ny * 2.13f + 50f) * 0.3f
                        + Mathf.PerlinNoise(nx * 4.27f + 25f, ny * 4.27f + 25f) * 0.1f;
                    radialOffsets[a] = (n - 0.5f) * 2f * (noiseAmplitude * 0.1f) * scaledThickness;
                }
            }

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = (x + 0.5f - halfRes) / halfRes;
                    float dy = (y + 0.5f - halfRes) / halfRes;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    float localInner = innerRadius;
                    float localOuter = outerRadius;

                    if (noiseAmplitude > 0f)
                    {
                        float angle = Mathf.Atan2(dy, dx);
                        if (angle < 0f) angle += 2f * Mathf.PI;
                        float sampleF = (angle / (2f * Mathf.PI)) * angleSamples;
                        int idx0 = (int)sampleF % angleSamples;
                        int idx1 = (idx0 + 1) % angleSamples;
                        float frac = sampleF - (int)sampleF;
                        float offset = Mathf.Lerp(radialOffsets[idx0], radialOffsets[idx1], frac);

                        localInner += offset;
                        localOuter += offset;
                    }

                    // Distance from the ring band, in texture-space units
                    float bandDist;
                    if (dist < localInner)
                        bandDist = localInner - dist;
                    else if (dist > localOuter)
                        bandDist = dist - localOuter;
                    else
                        bandDist = 0f;

                    // Normalize to 0-1 using available fade space (distance from band edge to texture boundary)
                    // Use outerRadius as reference since that's where the visible edge typically is
                    float fadeRange = Mathf.Max(1f - outerRadius, 0.05f);
                    float normalizedDist = bandDist / fadeRange;

                    float alpha = ComputeGradientFalloff(normalizedDist, gradient, falloff);
                    alpha = Mathf.Clamp01(alpha);

                    if (invert) alpha = 1f - alpha;

                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        public static Texture2D GeneratePolygon(
            int resolution, int sideCount, float roundness,
            float gradient, float falloff, bool invert)
        {
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[resolution * resolution];
            float halfRes = resolution * 0.5f;
            float shapeScale = 1f / 0.7f;

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = (x + 0.5f - halfRes) / halfRes;
                    float dy = (y + 0.5f - halfRes) / halfRes;

                    float dist = PolygonDistance(dx * shapeScale, dy * shapeScale, sideCount, roundness);

                    float alpha = ComputeGradientFalloff(dist, gradient, falloff);
                    if (invert) alpha = 1f - alpha;

                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        static float ComputeGradientFalloff(float dist, float gradient, float falloff)
        {
            if (dist >= 1f) return 0f;
            if (dist <= 0f) return 1f;

            float gradientStart = Mathf.Clamp01(gradient);

            if (dist < gradientStart)
                return 1f;

            float t = (dist - gradientStart) / Mathf.Max(1f - gradientStart, 0.001f);
            t = Mathf.Clamp01(t);

            // Smooth hermite interpolation for softer edges than power curve
            float smooth = t * t * (3f - 2f * t);
            return 1f - smooth;
        }

        static float PolygonDistance(float x, float y, int sides, float roundness)
        {
            float angle = Mathf.Atan2(y, x);
            float radius = Mathf.Sqrt(x * x + y * y);

            float segmentAngle = 2f * Mathf.PI / sides;
            float halfSegment = segmentAngle * 0.5f;
            float relativeAngle = Mathf.Repeat(angle + halfSegment, segmentAngle) - halfSegment;

            float polyRadius = Mathf.Cos(halfSegment) / Mathf.Cos(relativeAngle);
            polyRadius = Mathf.Lerp(polyRadius, 1f, roundness);

            return radius / Mathf.Max(polyRadius, 0.001f);
        }
    }
}
