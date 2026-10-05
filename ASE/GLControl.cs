using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace ASE
{
    public class GLControl : OpenGlControlBase
    {
        // Shaders — Desktop OpenGL Core Profile. The "#version" line is prepended at init
        // (see DesktopGlslVersion): the body only needs GLSL 1.40 features, and drivers that
        // stop at GL 3.1 — the Raspberry Pi's V3D, for one — reject "#version 150" outright.
        private const string VertexShaderDesktop = @"
            in vec2 aPos;
            in vec2 aTexCoord;
            out vec2 v_texCoord;

            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                v_texCoord  = aTexCoord;
            }";


        // Shaders — OpenGL ES 2.0 / 3.0  (ANGLE / Metal). GLSL ES 1.00 is accepted by every
        // ES context, including ES 3.x, so the "#version 100" line is fixed.
        private const string VertexShaderES = @"#version 100
            precision highp float;

            attribute vec2 aPos;
            attribute vec2 aTexCoord;
            varying vec2 v_texCoord;

            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                v_texCoord  = aTexCoord;
            }";


        // Shaders — the final pass, into Avalonia's framebuffer: the CRT program ("the screen")
        // or the plain one. Both sample the last texture of the chain through the same border
        // crop (uTexMin/uTexMax) and the same scaling (uSharp, Config.Scaling: 0 = bilinear, 1 =
        // sharp-bilinear — the blend between two texels is squeezed into exactly one output
        // pixel, so a non-integer scale still shows crisp pixels without the shimmer of NEAREST;
        // Themaister's shader). One body for desktop and ES, like every pass; the vertex
        // shaders are shared, and unlike the offscreen passes these read v_texCoord unflipped
        // because they draw the screen quad itself.
        //
        // The CRT model is the one the emulator always had, on purpose: a gentle sinusoidal
        // ripple over the lines (one period per ST line, +-8% at the slider's 1.0), a phosphor
        // mask, curvature, vignette, chromatic aberration, bloom and noise, composed in linear
        // light. A physically-minded beam model (a gaussian spot per line, wider the brighter,
        // unit area, fading out under two pixels per line) was tried in its place and rejected on
        // sight: at the same slider values it was far heavier, heavier read as blurrier, and the
        // old picture was described as exactly composite on a real television. If it ever comes back
        // it is a mode of its own, not a new meaning for the same slider. uSourceSize.y counts
        // the ST's *lines* whatever the chain scaled the texture to, so the ripple's pitch is
        // always one scanline. The mask has three geometries (uMaskType: aperture grille, slot
        // mask, shadow mask — the latter the original pattern) drawn in units of uMaskScale
        // output pixels, so a high-DPI screen gets phosphors the same physical size.
        private const string CrtShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uSourceSize;     // raw framebuffer size: ST columns/lines (scanline pitch)
            uniform vec2 uTexSize;        // size of the texture actually sampled (chain output)
            uniform vec2 uOutputSize;     // viewport, in pixels
            uniform float uTime;
            uniform float uCurvature;
            uniform float uVignette;
            uniform float uScanline;
            uniform float uChromAb;
            uniform float uBloom;
            uniform float uMask;
            uniform float uNoise;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;
            uniform float uSharp;
            uniform float uMaskType;
            uniform float uMaskScale;

            float hash12(vec2 p) {
                vec3 p3  = fract(vec3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.x + p3.y) * p3.z);
            }

            // Sharp-bilinear: only the last output pixel of each texel interpolates.
            vec2 sharpUv(vec2 uv) {
                vec2 t = uv * uTexSize;
                vec2 scale = max(uOutputSize / (uTexSize * (uTexMax - uTexMin)), vec2(1.0));
                vec2 rr = 0.5 - 0.5 / scale;
                vec2 cd = fract(t) - 0.5;
                vec2 f = (cd - clamp(cd, -rr, rr)) * scale + 0.5;
                return mix(t, floor(t) + f, uSharp) / uTexSize;
            }

            vec3 fetch(vec2 uv) { return TEXFETCH(uTexture, sharpUv(uv)).rgb; }
            vec3 toLin(vec3 c) { return pow(c, vec3(2.2)); }

            vec3 fetchCa(vec2 uv, vec2 caOff) {
                vec3 c;
                c.r = fetch(uv + caOff).r;
                c.g = fetch(uv).g;
                c.b = fetch(uv - caOff).b;
                return c;
            }

            void main() {
                vec2 uv = V_TEXCOORD;
                float aspect = uOutputSize.x / uOutputSize.y;

                vec2 p = uv * 2.0 - 1.0;
                p.x *= aspect;
                float r2 = dot(p, p);
                p *= (1.0 + uCurvature * r2);
                p.x /= aspect;
                uv = p * 0.5 + 0.5;

                float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
                float feather = 0.010;
                float edge =
                    smoothstep(0.0, feather, uv.x) *
                    smoothstep(0.0, feather, uv.y) *
                    smoothstep(0.0, feather, 1.0 - uv.x) *
                    smoothstep(0.0, feather, 1.0 - uv.y);
                float screenMask = inside * edge;

                // Border crop: identity when borders are shown, the display sub-rectangle otherwise.
                uv = mix(uTexMin, uTexMax, uv);

                float dist = sqrt(r2);
                vec2 dir = normalize(p + vec2(1e-4));
                float ca = uChromAb * (0.0006 + 0.0018 * dist * dist);
                vec2 caOff = dir * ca;

                vec3 lin = toLin(fetchCa(uv, caOff));

                // Bloom: the glow of the bright parts, gathered from the neighbourhood.
                vec2 t = 1.0 / max(uTexSize, vec2(1.0));
                vec3 b =
                    fetch(uv) * 0.40 +
                    fetch(uv + vec2( t.x, 0.0)) * 0.15 +
                    fetch(uv + vec2(-t.x, 0.0)) * 0.15 +
                    fetch(uv + vec2(0.0,  t.y)) * 0.15 +
                    fetch(uv + vec2(0.0, -t.y)) * 0.15;
                b = toLin(b);
                vec3 bright = max(b - vec3(0.60), vec3(0.0));
                lin += bright * uBloom;

                // Scanlines: the ripple over the lines, one period per ST line; the slider's
                // 1.0 is +-8% and the rest of its range scales that.
                float ly = uv.y * uSourceSize.y;
                float scan = 0.92 + 0.08 * sin(6.2831853 * (ly + 0.15));
                lin *= mix(1.0, scan, uScanline);

                // Phosphor mask, in units of uMaskScale pixels.
                vec2 mp = floor(gl_FragCoord.xy / uMaskScale);
                float ph;
                float gap = 1.0;
                if (uMaskType < 0.5) {
                    // Aperture grille: straight vertical RGB stripes.
                    ph = mod(mp.x, 3.0);
                } else if (uMaskType < 1.5) {
                    // Slot mask: the stripes broken by a dark row every four, staggered per triad.
                    ph = mod(mp.x, 3.0);
                    float triad = floor(mp.x / 3.0);
                    float stagger = (mod(triad, 2.0) > 0.5) ? 2.0 : 0.0;
                    gap = (mod(mp.y + stagger, 4.0) < 1.0) ? 0.4 : 1.0;
                } else {
                    // Shadow mask: triads shifted half a pitch on alternate rows.
                    float shift = (mod(mp.y, 2.0) > 0.5) ? 1.5 : 0.0;
                    ph = mod(mp.x + shift, 3.0);
                }
                vec3 dots = vec3(0.25);
                if (ph < 1.0) dots.r = 1.0;
                else if (ph < 2.0) dots.g = 1.0;
                else dots.b = 1.0;
                lin *= mix(vec3(1.0), dots * gap, uMask);

                float vig = 1.0 - uVignette * smoothstep(0.55, 1.25, dist);
                lin *= vig;

                float n = hash12(gl_FragCoord.xy + uTime * 60.0);
                lin += (n - 0.5) * 0.02 * uNoise;

                vec3 outCol = pow(max(lin, 0.0), vec3(1.0 / 2.2));
                outCol = mix(vec3(0.0), outCol, screenMask);

                FRAGOUT = vec4(outCol, 1.0);
            }";

        // Shaders — "plain" path: the same blit with no post-processing at all, used when
        // Config.DisableCrtEffects is on. It is a separate program rather than the CRT one with
        // every uniform at 0 because a weight of 0 costs the GPU exactly the same as any other
        // value: the fragment program still runs the beam, the bloom taps, the noise hash and
        // the two gamma conversions for every pixel on screen. On a Raspberry Pi's V3D that is
        // the difference worth having. The border crop and the sharp scaling are kept — they
        // are a UV remap, not an effect.
        private const string PlainShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexSize;
            uniform vec2 uOutputSize;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;
            uniform float uSharp;

            vec2 sharpUv(vec2 uv) {
                vec2 t = uv * uTexSize;
                vec2 scale = max(uOutputSize / (uTexSize * (uTexMax - uTexMin)), vec2(1.0));
                vec2 rr = 0.5 - 0.5 / scale;
                vec2 cd = fract(t) - 0.5;
                vec2 f = (cd - clamp(cd, -rr, rr)) * scale + 0.5;
                return mix(t, floor(t) + f, uSharp) / uTexSize;
            }

            void main() {
                vec2 uv = mix(uTexMin, uTexMax, V_TEXCOORD);
                FRAGOUT = vec4(TEXFETCH(uTexture, sharpUv(uv)).rgb, 1.0);
            }";

        private static string CrtFragmentDesktop   => ToDesktopFragment(CrtShaderBody);
        private static string CrtFragmentES        => ToEsFragment(CrtShaderBody);
        private static string PlainFragmentDesktop => ToDesktopFragment(PlainShaderBody);
        private static string PlainFragmentES      => ToEsFragment(PlainShaderBody);

        // Shaders — dithering colorization, first of the two passes the filter is made of. Both
        // are passes of their own, rendered into FBOs at the emulator's own resolution
        // (832x288 / 640x400) and only when a new frame has been published; the CRT or plain
        // program then samples the result instead of the raw texture. Doing it per *texel*
        // rather than per screen pixel is the whole point: the picture is upscaled 2-4x and the
        // GL thread renders free-running, so the same work inside the CRT shader would evaluate
        // its taps tens of times per texel per emulated frame.
        //
        // What this first pass does: the ST shows 16 colours out of 512 (4096 on an STE), so
        // gradients and polygon shading are drawn as two-colour patterns. A CRT's limited
        // bandwidth merged those into a single intermediate tone; a sharp LCD shows the pattern
        // instead. This finds the patterns and replaces them with a real RGB average — a colour
        // outside the ST palette altogether, which is the point of the feature.
        //
        // What it must NOT touch is ordinary detail: text, one-pixel sprite outlines, polygon
        // edges. A plain blur destroys those, so every test below is about **periodicity**, not
        // sharpness, and every mean only ever averages the two colours of the pattern it found.
        // Four detectors, applied in order so that the more specific ones override the rest:
        //
        //   0. Agreement with the 2x2 cell over a 7x7 window (period 4). Every ordered dither of
        //      period 4 — the Bayer cell, which is what the *fine* steps of a gradient are drawn
        //      with, at 1-in-8, 3-in-8, 5-in-8 and 7-in-8 density — tiles one 4x4 cell across an
        //      area, so the cell the centre belongs to is taken as a prediction and the whole 7x7
        //      window is held to it. Two guards keep structure out, and both are needed: the cell
        //      must contain **exactly two colours** (text over a two-colour sky brings a third),
        //      and the agreement is counted with **uniform** weights rather than the kernel
        //      below — the kernel barely weighs the rim of the window, which is precisely where a
        //      two-row band shows that it is not a repeating pattern. On top of that the
        //      agreement is checked again over the positions the prediction fills with the
        //      *scarcer* of the two colours: a one-pixel diagonal gets the background right and
        //      those positions wrong, which is what tells a dither from a line drawn in two
        //      colours.
        //   1. Agreement with the 2x2 cell over 5x5 (period 2), proportional. The cell the centre
        //      belongs to is the prediction, the weight that agrees fades the effect in
        //      (0.80 -> 0.95 of the total), and the texels that disagree are left OUT of the
        //      mean, so beside a letter or an edge the detail is excluded and the flat colour
        //      around it averages to itself. This is what handles areas of dots at 1-in-4 and
        //      3-in-4 density, where the mean is exact. Guard: a cell that is plain stripes must
        //      keep alternating out to distance 3 on its axis, because the gap between two
        //      one-pixel letter stems ("r" next to "p": black-white-black-white-black) is exactly
        //      five columns of vertical-stripe dither and nothing less can tell them apart.
        //   2. Chequerboard: the four diagonal neighbours match the centre and at least three of
        //      the four orthogonal ones are one same other colour. That is a two-pixel-wide band
        //      of chequer between two flat colours — the single most common shading device in
        //      ST game art, and too narrow for the 5x5 agreement to reach 80%. A single 2x2 cell
        //      on its own does not pass (only two orthogonals differ), a diagonal line does not
        //      (only two diagonals match), a stroke does not (no diagonal matches).
        //   3. Horizontal run of alternation: the texel sits inside a run of at least five
        //      alternating pixels along its row. This is the pixel-artist's dither — one or two
        //      rows of "abab" laid along the contour of a shaded shape, staggered as the contour
        //      moves — and it is also, not by coincidence, precisely what a CRT blurred: its
        //      bandwidth limit is horizontal, so alternation along a scanline merged into a tone
        //      while alternation between scanlines stayed sharp. The run is measured as the
        //      maximal run containing the centre (7 taps), which is what keeps the run's *end*
        //      texels in — each of those still belongs to a run of five or more — while the gap
        //      of a letter pair ("abab", four) stays out. Guard: if the row above or below repeats
        //      the same alternation in the same phase, these are vertical strokes one pixel apart
        //      (the top and bottom rows of two stems), not a dither run.
        //
        // The weights are the separable kernel [1,2,3,4,3,2,1]/16, which is the convolution of
        // two 4-wide boxes and therefore carries the **same total weight on every residue mod 4**
        // (4+4+4+4) and mod 2 (8+8). That makes the mean *exact* for both period-2 and period-4
        // patterns — 50% on a chequerboard, 25% on a 1-in-4 pattern, 12.5% on a 1-in-8 one —
        // where an odd-sized box would leave a few percent of the pattern behind as a residue.
        // Detectors 1-3 keep the [1,2,2,2,1]/8 kernel they were calibrated with, which is exact
        // for period 2. The mean is taken in the stored (gamma) space on purpose: on real
        // hardware the merging happened to the DAC's video signal, *before* the tube's gamma, so
        // averaging in linear light would come out visibly too bright.
        //
        // Every texel that any detector fired on is **marked in the alpha channel**, with the
        // strength it fired at. Alpha is otherwise wasted here (the CRT pass reads .rgb), and
        // there is no MRT in GL ES 2.0, so it is the only side channel available — the gradient
        // pass below is built entirely on it, and gets a map of "here there is a pattern and no
        // detail" for free, which is what lets it filter with a large radius safely.
        //
        // Colour comparisons are exact to within 0.005: palette entries expand to 8 bits in steps
        // of 36/255 on an ST and 17/255 on an STE, so there is no chance of two different colours
        // reading as one. It does require NEAREST sampling — a LINEAR tap landing a hair off the
        // texel centre would blend the neighbours into the comparison — which RunColorizeChain sets
        // for the duration of the passes and restores afterwards.
        private const string DitherShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size

            bool same(vec3 a, vec3 b) {
                return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b) < 0.005;
            }

            float w5(int k) { return (k == 0 || k == 4) ? 1.0 : 2.0; }
            float w7(int k) {
                return (k == 0 || k == 6) ? 1.0
                     : ((k == 1 || k == 5) ? 2.0
                     : ((k == 2 || k == 4) ? 3.0 : 4.0));
            }

            void main() {
                // The pass draws into an FBO with the same quad as the screen, so its row 0 is the
                // one the screen shows last. Flipping here keeps the intermediate texture in the
                // emulator framebuffer's own orientation, and the passes after it sample exactly
                // as they sample the raw texture.
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);

                vec3 c = TEXFETCH(uTexture, uv).rgb;

                // Horizontal step, in texels, of one *ST* pixel. Low resolution is written into
                // the framebuffer pixel-doubled (832 columns for 416 half-cycles), so its finest
                // chequerboard has period 4 in texels where medium and high resolution have 2.
                // Read it off the picture rather than plumbing the shifter mode through to the GL
                // thread: the resolution can change from one scanline to the next.
                //
                // What identifies the doubling is that EVERY column pairs up with a neighbour on
                // a single phase, so it is checked over three rows and four offsets. Asking only
                // whether one neighbour matches (which it was) is true almost everywhere in any
                // sparse dither by pure chance, and reading a period-4 pattern with a step of two
                // texels made the detector below see nothing at all.
                vec3 row[27];
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 9; i++)
                        row[j * 9 + i] = TEXFETCH(uTexture, uv + vec2(float(i - 4), float(j - 1)) * uTexel).rgb;

                bool phaseA = true;
                bool phaseB = true;
                for (int j = 0; j < 3; j++) {
                    for (int k = 0; k < 4; k++) {
                        if (!same(row[j * 9 + 2 * k],     row[j * 9 + 2 * k + 1])) phaseA = false;
                        if (!same(row[j * 9 + 2 * k + 1], row[j * 9 + 2 * k + 2])) phaseB = false;
                    }
                }
                float hx = (phaseA || phaseB) ? 2.0 : 1.0;

                // Vertically there is exactly one framebuffer row per scanline in every mode, so
                // the step is fixed. Detecting it the same way would be wrong: a 1-in-4 dither is
                // dense rows alternating with plain ones, and a row matching its neighbour would
                // make the filter skip the very rows that carry the pattern.
                vec2 stepUV = vec2(hx * uTexel.x, uTexel.y);

                // The 7x7 neighbourhood in ST pixels, indexed (j+3)*7+(i+3) with the centre at
                // (0,0). Detectors 1-3 work on the 5x5 inside it, and the guards on the taps at
                // distance 3 along the axes.
                vec3 win[49];
                for (int j = 0; j < 7; j++)
                    for (int i = 0; i < 7; i++)
                        win[j * 7 + i] = TEXFETCH(uTexture, uv + vec2(float(i - 3), float(j - 3)) * stepUV).rgb;

                vec3 W3 = win[3 * 7 + 0], E3 = win[3 * 7 + 6];
                vec3 N3 = win[0 * 7 + 3], S3 = win[6 * 7 + 3];

                vec3 N  = win[2 * 7 + 3], S  = win[4 * 7 + 3];
                vec3 W  = win[3 * 7 + 2], E  = win[3 * 7 + 4];
                vec3 NW = win[2 * 7 + 2], NE = win[2 * 7 + 4];
                vec3 SW = win[4 * 7 + 2], SE = win[4 * 7 + 4];
                vec3 W2 = win[3 * 7 + 1], E2 = win[3 * 7 + 5];
                vec3 N2 = win[1 * 7 + 3], S2 = win[5 * 7 + 3];

                vec3 result = c;
                float mark = 0.0;

                // ---- 0. Ordered pattern of period 4 (the Bayer cell): the fine steps of a
                // gradient. The cell is the 4x4 block anchored at the centre; every position of
                // the window is predicted from it, and each position of the window is reached
                // exactly once as (a - 4*t1, b - 4*t2).
                vec3 k0 = win[3 * 7 + 3];
                vec3 k1 = k0;
                bool has1  = false;
                bool three = false;
                float n0   = 0.0;
                for (int b = 0; b < 4; b++) {
                    for (int a = 0; a < 4; a++) {
                        vec3 v = win[(b + 3) * 7 + (a + 3)];
                        if (same(v, k0)) n0 += 1.0;
                        else if (!has1) { k1 = v; has1 = true; }
                        else if (!same(v, k1)) three = true;
                    }
                }

                if (has1 && !three) {
                    vec3 minor = ((16.0 - n0) <= n0) ? k1 : k0;
                    float unif = 0.0, agree = 0.0, agMin = 0.0, totMin = 0.0;
                    vec3 sum4 = vec3(0.0);
                    for (int b = 0; b < 4; b++) {
                        for (int a = 0; a < 4; a++) {
                            vec3 pred = win[(b + 3) * 7 + (a + 3)];
                            for (int t2 = 0; t2 < 2; t2++) {
                                for (int t1 = 0; t1 < 2; t1++) {
                                    if (a - 4 * t1 >= -3 && b - 4 * t2 >= -3) {
                                        vec3 v = win[(b - 4 * t2 + 3) * 7 + (a - 4 * t1 + 3)];
                                        bool hit = same(v, pred);
                                        if (same(pred, minor)) {
                                            totMin += 1.0;
                                            if (hit) agMin += 1.0;
                                        }
                                        if (hit) {
                                            float w = w7(a - 4 * t1 + 3) * w7(b - 4 * t2 + 3);
                                            unif  += 1.0;
                                            agree += w;
                                            sum4  += w * v;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    float b4 = clamp((unif / 49.0 - 0.90) / 0.10, 0.0, 1.0);
                    if (totMin > 0.0 && agMin / totMin < 0.80) b4 = 0.0;
                    if (b4 > 0.0) {
                        result = mix(result, sum4 / agree, b4);
                        mark = max(mark, b4);
                    }
                }

                // ---- 3. Horizontal run of alternation.
                if (!same(W, c) && same(W, E)) {
                    vec3 o = W;
                    float left  = same(W2, c) ? (same(W3, o) ? 2.0 : 1.0) : 0.0;
                    float right = same(E2, c) ? (same(E3, o) ? 2.0 : 1.0) : 0.0;
                    bool ok = (3.0 + left + right) >= 5.0;
                    // Same alternation, same phase, in the row above or below: two strokes one
                    // pixel apart, not a run.
                    if (same(S, c) && same(SW, o) && same(SE, o)) ok = false;
                    if (same(N, c) && same(NW, o) && same(NE, o)) ok = false;
                    if (ok) {
                        vec3 sumR = vec3(0.0);
                        float wR = 0.0;
                        for (int i = 0; i < 5; i++) {
                            vec3 s = win[3 * 7 + (i + 1)];
                            if (same(s, c) || same(s, o)) { sumR += w5(i) * s; wR += w5(i); }
                        }
                        result = sumR / wR;
                        mark = 1.0;
                    }
                }

                // ---- 2. Chequerboard.
                if (same(NW, c) && same(NE, c) && same(SW, c) && same(SE, c)) {
                    vec3 o = !same(N, c) ? N : (!same(S, c) ? S : (!same(W, c) ? W : E));
                    if (!same(o, c)) {
                        float k = (same(N, o) ? 1.0 : 0.0) + (same(S, o) ? 1.0 : 0.0) +
                                  (same(W, o) ? 1.0 : 0.0) + (same(E, o) ? 1.0 : 0.0);
                        bool two = (same(N, o) || same(N, c)) && (same(S, o) || same(S, c)) &&
                                   (same(W, o) || same(W, c)) && (same(E, o) || same(E, c));
                        if (k >= 3.0 && two) {
                            vec3 sumC = vec3(0.0);
                            float wC = 0.0;
                            for (int j = 0; j < 5; j++) {
                                for (int i = 0; i < 5; i++) {
                                    vec3 s = win[(j + 1) * 7 + (i + 1)];
                                    if (same(s, c) || same(s, o)) {
                                        float w = w5(i) * w5(j);
                                        sumC += w * s;
                                        wC += w;
                                    }
                                }
                            }
                            result = sumC / wC;
                            mark = 1.0;
                        }
                    }
                }

                // ---- 1. Agreement with the 2x2 cell (top priority, proportional).
                vec3 c10 = E, c01 = S, c11 = SE;
                vec3 sumA = vec3(0.0);
                float wA = 0.0;
                for (int j = 0; j < 5; j++) {
                    for (int i = 0; i < 5; i++) {
                        float px = mod(float(i - 2), 2.0);
                        float py = mod(float(j - 2), 2.0);
                        vec3 expected = mix(mix(c, c10, px), mix(c01, c11, px), py);
                        vec3 s = win[(j + 1) * 7 + (i + 1)];
                        if (same(s, expected)) {
                            float w = w5(i) * w5(j);
                            sumA += w * s;
                            wA += w;
                        }
                    }
                }
                // wA is never zero: the centre always agrees with itself, and carries weight 4.
                float blend = clamp((wA / 64.0 - 0.80) / 0.15, 0.0, 1.0);

                // A stripe cell must keep alternating out to distance 3 on its axis, or it is the
                // gap between two letter stems (see the comment above the shader).
                bool vstripes = same(c, c01) && same(c10, c11) && !same(c, c10);
                bool hstripes = same(c, c10) && same(c01, c11) && !same(c, c01);
                if (vstripes && !(same(W2, c) && same(E2, c) && same(W3, c10) && same(E3, c10))) blend = 0.0;
                if (hstripes && !(same(N2, c) && same(S2, c) && same(N3, c01) && same(S3, c01))) blend = 0.0;

                result = mix(result, sumA / wA, blend);
                mark = max(mark, blend);

                FRAGOUT = vec4(result, mark);
            }";

        private static string DitherFragmentDesktop =>
            "in vec2 v_texCoord;\nout vec4 fragColor;\n" +
            DitherShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                            .Replace("TEXFETCH", "texture")
                            .Replace("FRAGOUT", "fragColor");

        private static string DitherFragmentES =>
            "#version 100\nprecision highp float;\nvarying vec2 v_texCoord;\n" +
            DitherShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                            .Replace("TEXFETCH", "texture2D")
                            .Replace("FRAGOUT", "gl_FragColor");

        // Shaders — gradient reconstruction, the second pass. Run twice, vertically and then
        // horizontally (uDir), each into its own FBO.
        //
        // What is left after the pass above is a *staircase*: the artist drew a gradient as bands
        // of increasing dither density — flat colour, 1-in-8, 1-in-4, chequer, 3-in-4… — and the
        // first pass turns each band into its own flat tone. The pattern is gone but the banding
        // is not, and on a large screen it is what still says "16 colours". This pass merges the
        // bands into a real ramp, which is the whole reason the ST's artwork can come out with
        // hundreds of shades where the machine only ever showed a handful.
        //
        // Three things make it safe to filter with a radius this large:
        //
        //   * It only ever reads texels the first pass MARKED (alpha > 0). Text, sprite outlines,
        //     polygon edges and flat art all have alpha 0: they are neither smoothed nor allowed
        //     to bleed into anything that is. That map is the reason this pass needs no detector
        //     of its own.
        //   * The range test is done **in chain** — each tap against the last accepted one, not
        //     against the centre. A ramp crosses four or five band steps in a row, which against
        //     the centre would leave the tolerance long before the far end, while a real edge
        //     still cuts because its jump happens between two consecutive taps.
        //   * A texel the first pass left half-converted (they happen where a window straddles two
        //     band densities) is skipped rather than allowed to cut the chain, up to two in a row;
        //     and when the centre itself is that texel — its two immediate neighbours agree with
        //     each other and not with it — it is treated as an outlier and kept out of its own
        //     mean. Without that, a quarter of the texels on a band boundary stayed at their
        //     original palette colour, as bright dots along the seam.
        //
        // Weights are gaussian with sigma 6 over a radius of 12 texels. The step is one texel and
        // not one ST pixel on purpose: at low resolution that halves the reach horizontally, which
        // is invisible (gradients in ST art are overwhelmingly vertical) and saves plumbing the
        // doubling through to a pass that has no other use for it — measured on the calibration
        // scene, one texel and two give the same ramp to four decimals.
        private const string GradientShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;
            uniform vec2 uDir;            // (0,1) vertical pass, (1,0) horizontal pass

            float dist1(vec3 a, vec3 b) {
                return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b);
            }

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec2 st = uDir * uTexel;

                vec4 c4 = TEXFETCH(uTexture, uv);
                vec3 c  = c4.rgb;

                vec4 pa = TEXFETCH(uTexture, uv - st);
                vec4 pb = TEXFETCH(uTexture, uv + st);

                // The centre is an outlier when both neighbours are dithered, agree with each
                // other, and disagree with it: a texel the first pass could not resolve.
                bool outlier = c4.a > 0.0 && pa.a > 0.0 && pb.a > 0.0 &&
                               dist1(pa.rgb, pb.rgb) < 0.35 &&
                               dist1(pa.rgb, c) > 0.35 && dist1(pb.rgb, c) > 0.35;

                vec3  seed = outlier ? 0.5 * (pa.rgb + pb.rgb) : c;
                float w0   = outlier ? 0.0 : 1.0;
                vec3  acc  = w0 * c;
                float ws   = w0;

                for (int s = 0; s < 2; s++) {
                    float sgn = (s == 0) ? -1.0 : 1.0;
                    vec3  ref2  = seed;
                    float skips = 0.0;
                    float alive = 1.0;
                    for (int k = 1; k <= 12; k++) {
                        vec4 t = TEXFETCH(uTexture, uv + st * (sgn * float(k)));
                        if (t.a <= 0.0) alive = 0.0;
                        if (alive > 0.0) {
                            if (dist1(t.rgb, ref2) > 0.35) {
                                skips += 1.0;
                                if (skips > 2.0) alive = 0.0;
                            } else {
                                skips = 0.0;
                                ref2  = t.rgb;
                                float w = exp(-float(k * k) / 72.0);
                                acc += w * t.rgb;
                                ws  += w;
                            }
                        }
                    }
                }

                FRAGOUT = vec4(ws > 0.0 ? acc / ws : c, c4.a);
            }";

        private static string GradientFragmentDesktop =>
            "in vec2 v_texCoord;\nout vec4 fragColor;\n" +
            GradientShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                              .Replace("TEXFETCH", "texture")
                              .Replace("FRAGOUT", "fragColor");

        private static string GradientFragmentES =>
            "#version 100\nprecision highp float;\nvarying vec2 v_texCoord;\n" +
            GradientShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                              .Replace("TEXFETCH", "texture2D")
                              .Replace("FRAGOUT", "gl_FragColor");

        // Shaders — SuperEagle, the "smooth borders" pass. One pass of its own, rendering into a
        // target **twice the size** of the emulator's framebuffer, which the CRT/plain pass then
        // samples in place of the raw picture.
        //
        // The filter is Derek Liauw Kie Fa's SuperEagle (the 2xSaI family, the same one MAME and
        // the libretro front-ends ship), ported straight from the original C: for every source
        // texel it looks at the 12 neighbours drawn below, decides which way the edge through it
        // runs, and fills the 2x2 output block with the tones that edge would have had at twice
        // the resolution. A staircase of whole pixels becomes a line with intermediate steps and
        // the picture reads as if the machine had more resolution than it has, while a flat area
        // (every neighbour equal) comes out as four copies of itself, untouched.
        //
        //          B1 B2
        //       4  5  6  S2      5 is the texel this fragment belongs to; the 2x2 block it
        //       1  2  3  S1      produces is biased towards 6 (right), 2 (below) and 3, which
        //          A1 A2         is where the original reads its edges from.
        //
        // Which of the four products a fragment gets is decided by where inside the source texel
        // it lands: the target is exactly 2x, so fract() of the source position is 0.25 or 0.75
        // on each axis and the choice is unambiguous. The offsets are the original's unchanged:
        // the framebuffer is uploaded with its first line at t = 0, so a texture coordinate
        // growing in y walks *down* the picture, exactly like the C code's row pointer.
        //
        // Two things about where this sits in the chain. It runs **after** the colorization
        // passes, never before: those detect dithering by the periodicity of the raw texels, and
        // a scaler run first would leave them nothing to find. And it works on texels, not on ST
        // pixels — a low-resolution line is written pixel-doubled into the framebuffer, so its
        // diagonals are 2-texel steps, which the filter reads as a shallower slope and smooths
        // accordingly. That is the same thing every front-end does to an already-doubled picture,
        // and it is why no equivalent of the dithering shader's step detection is needed here.
        //
        // Like the passes above it needs NEAREST sampling (it compares texels for equality) and
        // runs at most once per published frame; RunEaglePass sets the filtering and restores it.
        private const string EagleShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size
            uniform vec2 uSourceSize;     // source size, in texels

            bool eq(vec3 a, vec3 b) {
                return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b) < 0.005;
            }

            // The original's GetResult: how far the two candidate colours agree with a pair of
            // outlying texels. Positive votes for the first, negative for the second.
            float vote(vec3 a, vec3 b, vec3 c, vec3 d) {
                float x = 0.0;
                float y = 0.0;
                float r = 0.0;
                if (eq(a, c)) x += 1.0; else if (eq(b, c)) y += 1.0;
                if (eq(a, d)) x += 1.0; else if (eq(b, d)) y += 1.0;
                if (x <= 1.0) r += 1.0;
                if (y <= 1.0) r -= 1.0;
                return r;
            }

            vec3 tap(vec2 bs, float dx, float dy) {
                return TEXFETCH(uTexture, bs + vec2(dx, dy) * uTexel).rgb;
            }

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec2 sp = uv * uSourceSize;
                vec2 fp = fract(sp);                       // quarter of the texel: 0.25 or 0.75
                vec2 bs = (floor(sp) + 0.5) * uTexel;      // centre of the source texel

                vec3 cB1 = tap(bs,  0.0, -1.0);
                vec3 cB2 = tap(bs,  1.0, -1.0);
                vec3 c4  = tap(bs, -1.0,  0.0);
                vec3 c5  = tap(bs,  0.0,  0.0);
                vec3 c6  = tap(bs,  1.0,  0.0);
                vec3 cS2 = tap(bs,  2.0,  0.0);
                vec3 c1  = tap(bs, -1.0,  1.0);
                vec3 c2  = tap(bs,  0.0,  1.0);
                vec3 c3  = tap(bs,  1.0,  1.0);
                vec3 cS1 = tap(bs,  2.0,  1.0);
                vec3 cA1 = tap(bs,  0.0,  2.0);
                vec3 cA2 = tap(bs,  1.0,  2.0);

                vec3 p1a, p1b, p2a, p2b;   // top-left, top-right, bottom-left, bottom-right

                if (eq(c2, c6) && !eq(c5, c3)) {
                    // Edge running one way: the 2/6 colour owns the anti-diagonal of the block.
                    p1b = c2;
                    p2a = c2;

                    if (eq(c1, c2) || eq(c6, cB2))
                        p1a = mix(c2, mix(c2, c5, 0.5), 0.5);
                    else
                        p1a = mix(c5, c6, 0.5);

                    if (eq(c6, cS2) || eq(c2, cA1))
                        p2b = mix(c2, mix(c2, c3, 0.5), 0.5);
                    else
                        p2b = mix(c2, c3, 0.5);
                }
                else if (eq(c5, c3) && !eq(c2, c6)) {
                    // The other way: the 5/3 colour owns the diagonal.
                    p1a = c5;
                    p2b = c5;

                    if (eq(cB1, c5) || eq(c3, cS1))
                        p1b = mix(c5, mix(c5, c6, 0.5), 0.5);
                    else
                        p1b = mix(c5, c6, 0.5);

                    if (eq(c3, cA2) || eq(c4, c5))
                        p2a = mix(c5, mix(c5, c2, 0.5), 0.5);
                    else
                        p2a = mix(c2, c3, 0.5);
                }
                else if (eq(c5, c3) && eq(c2, c6)) {
                    // Both diagonals present — a crossing. The wider neighbourhood votes on
                    // which of the two colours is the line and which is the background.
                    float r = vote(c6, c5, c1,  cA1)
                            + vote(c6, c5, c4,  cB1)
                            + vote(c6, c5, cA2, cS1)
                            + vote(c6, c5, cB2, cS2);

                    if (r > 0.0) {
                        p1b = c2;
                        p2a = c2;
                        p1a = mix(c5, c6, 0.5);
                        p2b = p1a;
                    }
                    else if (r < 0.0) {
                        p1a = c5;
                        p2b = c5;
                        p1b = mix(c5, c6, 0.5);
                        p2a = p1b;
                    }
                    else {
                        p1a = c5;
                        p2b = c5;
                        p1b = c2;
                        p2a = c2;
                    }
                }
                else {
                    // No edge to follow: each corner is weighted three parts towards the texel
                    // nearest it, which leaves a flat area exactly as it was.
                    p1a = (c5 + c5 + c5 + mix(c2, c6, 0.5)) * 0.25;
                    p2b = (c3 + c3 + c3 + mix(c2, c6, 0.5)) * 0.25;
                    p1b = (c6 + c6 + c6 + mix(c5, c3, 0.5)) * 0.25;
                    p2a = (c2 + c2 + c2 + mix(c5, c3, 0.5)) * 0.25;
                }

                vec3 top = (fp.x < 0.5) ? p1a : p1b;
                vec3 bot = (fp.x < 0.5) ? p2a : p2b;

                FRAGOUT = vec4((fp.y < 0.5) ? top : bot, 1.0);
            }";

        private static string EagleFragmentDesktop =>
            "in vec2 v_texCoord;\nout vec4 fragColor;\n" +
            EagleShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                           .Replace("TEXFETCH", "texture")
                           .Replace("FRAGOUT", "fragColor");

        private static string EagleFragmentES =>
            "#version 100\nprecision highp float;\nvarying vec2 v_texCoord;\n" +
            EagleShaderBody.Replace("V_TEXCOORD", "v_texCoord")
                           .Replace("TEXFETCH", "texture2D")
                           .Replace("FRAGOUT", "gl_FragColor");

        // Shaders — video signal: PAL composite or RF (aerial), the first pass of the
        // chain because it is what the television received. An RGB monitor got the three colour
        // signals apart; a TV got them folded into one — luma plus the two chroma components
        // modulated onto a 4.43 MHz subcarrier in quadrature, V's sign flipping every line — and
        // had to separate them again with filters, which is where the look comes from: colour
        // smeared horizontally (chroma bandwidth ~1.3 MHz), luma slightly soft, and *cross-colour*
        // — fine luma detail near the subcarrier frequency read back as colour, which is why a
        // one-pixel dither on a TV shimmered in rainbow stripes. That last one is deliberate: a
        // low-resolution pixel alternation is 4 MHz, right next to the subcarrier. The PAL delay
        // line (chroma averaged with the line above) halves the vertical colour resolution too.
        //
        // Per texel: the signal is regenerated from the source over a window (the ST's 16 MHz
        // half-cycle grid, 0.277 subcarrier cycles per texel; 0.7516 cycles left over per line
        // and a quarter cycle per frame, which is what makes the dot pattern crawl), chroma is
        // synchronously demodulated under a raised-cosine window on this row and the one above,
        // and luma is the signal with that chroma re-modulated and taken back out of it — the
        // way a set separates the two, and an adaptive notch exactly as wide as the chroma band
        // that costs the sharpness nothing — under a gaussian whose width is the luma
        // softness. Everything separated that way carries crosstalk, and how much of
        // it survives is the third knob: the luma and the chroma are computed a second time
        // straight from the source under the identical windows - same bandwidth, no carrier to
        // leak either way - and Config.RfArtefacts / CompositeArtefacts mix between the two. That
        // is the difference in kind between a good decoder and a bad one, and the dial the look is
        // toned down with; the other two, the chroma radius and the luma softness, only set how
        // far the colour smears and how soft the brightness is. RF is composite with all three
        // turned up, and nothing else. Runs once per published frame at the source
        // resolution; the pattern-based corrections after it are bypassed while it is on, since
        // it leaves them no patterns to find.
        private const string SignalShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size
            uniform vec2 uSourceSize;     // source size, in texels
            uniform float uChromaRadius;  // half-width of the chroma window, in texels (2..8)
            uniform float uLumaSoftness;  // luma blur, 0..1: a gaussian of sigma 0.25 + 1.25 * this, in texels
            uniform float uArtefacts;     // how much of the crosstalk the decoder lets through, 0..1
            uniform float uFrame;         // frame counter mod 8: the subcarrier phase drifts frame to frame

            const float PI = 3.14159265;
            const float SC_PER_TEXEL = 0.2771011;   // 4.43361875 MHz / 16 MHz
            const float SC_PER_LINE  = 0.7516;      // fractional part of 283.7516 cycles per line

            float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

            // The source texel at offset k on row r (0 this row, -1 the one above), as Y, U, V.
            vec3 sourceYuv(vec2 uv, float k, float r) {
                vec3 c = TEXFETCH(uTexture, uv + vec2(k, r) * uTexel).rgb;
                float y = luma(c);
                return vec3(y, 0.492 * (c.b - y), 0.877 * (c.r - y));
            }

            // ... folded onto the single wire the television was handed.
            float modulate(vec3 yuv, float ph, float vs) {
                return yuv.x + yuv.y * sin(ph) + yuv.z * vs * cos(ph);
            }

            // One row's chroma under a raised-cosine window, twice over: xy demodulated out of the
            // composite signal, crosstalk and all, and zw taken straight from the source under the
            // same window - what a perfect separator would have recovered. uArtefacts picks
            // between them, and it is the only difference in kind between a good set and a bad one.
            // Normalised by half the window's weight, not by its own sum of sin^2 / cos^2: the
            // latter is exact for a flat colour but weights the taps unevenly, and the luma
            // cancellation below relies on this demodulation being, re-modulated, the same thing
            // as a zero-phase band-pass at the carrier - which it is only with a symmetric
            // weighting. (The few percent a short window is off on a flat colour, the window's
            // response at twice the carrier, is the lesser evil, and small.)
            vec4 chroma(vec2 uv, float r, float phase0, float vs, float radius) {
                vec2 dem = vec2(0.0);
                vec2 cln = vec2(0.0);
                float wsum = 0.0;
                for (int k = -8; k <= 8; k++) {
                    float fk = float(k);
                    float w = (abs(fk) <= radius) ? 0.5 + 0.5 * cos(PI * fk / (radius + 1.0)) : 0.0;
                    vec3 yuv = sourceYuv(uv, fk, r);
                    float ph = phase0 + fk * 2.0 * PI * SC_PER_TEXEL;
                    dem  += w * modulate(yuv, ph, vs) * vec2(sin(ph), cos(ph) * vs);
                    cln  += w * yuv.yz;
                    wsum += w;
                }
                return vec4(2.0 * dem, cln) / wsum;
            }

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                float row = floor(uv.y * uSourceSize.y);
                float col = floor(uv.x * uSourceSize.x);

                float frameTerm = 2.0 * PI * 0.25 * uFrame;
                float ph0 = 2.0 * PI * (SC_PER_LINE * row + SC_PER_TEXEL * col) + frameTerm;
                float ph1 = 2.0 * PI * (SC_PER_LINE * (row - 1.0) + SC_PER_TEXEL * col) + frameTerm;
                float vs0 = (mod(row + uFrame, 2.0) < 1.0) ? 1.0 : -1.0;
                float vs1 = -vs0;

                // Chroma first, through the delay line: this row and the one above, averaged.
                vec4 c0 = chroma(uv, 0.0, ph0, vs0, uChromaRadius);
                vec4 c1 = chroma(uv, -1.0, ph1, vs1, uChromaRadius);
                vec4 cd = 0.5 * (c0 + c1);
                vec2 c = mix(cd.zw, cd.xy, uArtefacts);

                // Luma: the signal with the chroma just decoded on this row re-modulated and taken
                // back out of it (Y = composite - C, which is how a set separates the two), under
                // a gaussian whose width is the softness: a quarter texel at 0 - as sharp as the
                // wire carries, a low-res pixel comes through whole - to a texel and a half at 1,
                // the luma of a poor tuner. No fixed notch is needed: the cancellation is the
                // notch, and an adaptive one, exactly as wide as the chroma band, so it costs the
                // sharpness nothing (the one-period box that used to do this job halved the
                // contrast of every single pixel and was the blur that was complained about).
                // What it leaves behind is real: at a colour edge the decoded chroma is a blend
                // of both sides, the carrier there is not fully cancelled and crawls - the dot
                // crawl of every composite picture. The clean luma is the same gaussian over the
                // source brightness; uArtefacts mixes between the two.
                float sigma = 0.25 + 1.25 * uLumaSoftness;
                float y = 0.0;
                float yc = 0.0;
                float wl = 0.0;
                for (int k = -4; k <= 4; k++) {
                    float fk = float(k);
                    float w = exp(-0.5 * fk * fk / (sigma * sigma));
                    vec3 yuv = sourceYuv(uv, fk, 0.0);
                    float ph = ph0 + fk * 2.0 * PI * SC_PER_TEXEL;
                    y  += w * (modulate(yuv, ph, vs0) - (c0.x * sin(ph) + c0.y * cos(ph) * vs0));
                    yc += w * yuv.x;
                    wl += w;
                }
                y = mix(yc, y, uArtefacts) / wl;

                vec3 rgb;
                rgb.r = y + 1.140 * c.y;
                rgb.g = y - 0.395 * c.x - 0.581 * c.y;
                rgb.b = y + 2.032 * c.x;

                FRAGOUT = vec4(clamp(rgb, 0.0, 1.0), 1.0);
            }";

        // Shaders — xBR (level 2), the second choice of edge smoothing next to SuperEagle, and
        // the better one on curves: Hyllian's xBR-lv2 (copyright (C) 2011-2016 Hyllian,
        // sergiogdb@gmail.com, MIT licence; the libretro glsl-shaders version), ported with the
        // scale fixed at 2 and the "small details" variant left out. Where SuperEagle looks at
        // 12 neighbours and picks a diagonal, xBR looks at 21, measures the weighted colour
        // distance along the two possible edge directions through the texel, and interpolates
        // along the winner with three slopes (45, 30 and 60 degrees) — a curve comes out as a
        // curve, not a set of 45-degree facets. Same slot, target and rules as SuperEagle: 2x,
        // NEAREST on the input, after the colorization chain and before the anti-aliasing.
        private const string XbrShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size
            uniform vec2 uSourceSize;     // source size, in texels

            const float XBR_SCALE = 2.0;
            const float XBR_EQ_THRESHOLD = 15.0;
            const float XBR_LV2_COEFFICIENT = 2.0;

            const vec3 rgbw = vec3(14.352, 28.176, 5.472);

            const vec4 delta   = vec4(1.0 / XBR_SCALE);
            const vec4 delta_l = vec4(0.5 / XBR_SCALE, 1.0 / XBR_SCALE, 0.5 / XBR_SCALE, 1.0 / XBR_SCALE);
            const vec4 delta_u = vec4(1.0 / XBR_SCALE, 0.5 / XBR_SCALE, 1.0 / XBR_SCALE, 0.5 / XBR_SCALE);

            const vec4 Ao = vec4( 1.0, -1.0, -1.0,  1.0);
            const vec4 Bo = vec4( 1.0,  1.0, -1.0, -1.0);
            const vec4 Co = vec4( 1.5,  0.5, -0.5,  0.5);
            const vec4 Ax = vec4( 1.0, -1.0, -1.0,  1.0);
            const vec4 Bx = vec4( 0.5,  2.0, -0.5, -2.0);
            const vec4 Cx = vec4( 1.0,  1.0, -0.5,  0.0);
            const vec4 Ay = vec4( 1.0, -1.0, -1.0,  1.0);
            const vec4 By = vec4( 2.0,  0.5, -2.0, -0.5);
            const vec4 Cy = vec4( 2.0,  0.0, -1.0,  0.5);
            const vec4 Ci = vec4(0.25);

            vec4 df(vec4 A, vec4 B) { return abs(A - B); }
            vec4 diff(vec4 A, vec4 B) { return vec4(notEqual(A, B)); }
            vec4 eq(vec4 A, vec4 B) { return step(df(A, B), vec4(XBR_EQ_THRESHOLD)); }
            vec4 neq(vec4 A, vec4 B) { return vec4(1.0) - eq(A, B); }

            vec4 wd(vec4 a, vec4 b, vec4 c, vec4 d, vec4 e, vec4 f, vec4 g, vec4 h) {
                return df(a, b) + df(a, c) + df(d, e) + df(d, f) + 4.0 * df(g, h);
            }

            float c_df(vec3 c1, vec3 c2) {
                vec3 d = abs(c1 - c2);
                return d.r + d.g + d.b;
            }

            vec3 tap(vec2 bs, float dx, float dy) {
                return TEXFETCH(uTexture, bs + vec2(dx, dy) * uTexel).rgb;
            }

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec2 sp = uv * uSourceSize;
                vec2 fp = fract(sp);
                vec2 bs = (floor(sp) + 0.5) * uTexel;

                //    A1 B1 C1
                // A0 A  B  C  C4
                // D0 D  E  F  F4
                // G0 G  H  I  I4
                //    G5 H5 I5
                vec3 A1 = tap(bs, -1.0, -2.0);
                vec3 B1 = tap(bs,  0.0, -2.0);
                vec3 C1 = tap(bs,  1.0, -2.0);
                vec3 A  = tap(bs, -1.0, -1.0);
                vec3 B  = tap(bs,  0.0, -1.0);
                vec3 C  = tap(bs,  1.0, -1.0);
                vec3 D  = tap(bs, -1.0,  0.0);
                vec3 E  = tap(bs,  0.0,  0.0);
                vec3 F  = tap(bs,  1.0,  0.0);
                vec3 G  = tap(bs, -1.0,  1.0);
                vec3 H  = tap(bs,  0.0,  1.0);
                vec3 I  = tap(bs,  1.0,  1.0);
                vec3 G5 = tap(bs, -1.0,  2.0);
                vec3 H5 = tap(bs,  0.0,  2.0);
                vec3 I5 = tap(bs,  1.0,  2.0);
                vec3 A0 = tap(bs, -2.0, -1.0);
                vec3 D0 = tap(bs, -2.0,  0.0);
                vec3 G0 = tap(bs, -2.0,  1.0);
                vec3 C4 = tap(bs,  2.0, -1.0);
                vec3 F4 = tap(bs,  2.0,  0.0);
                vec3 I4 = tap(bs,  2.0,  1.0);

                vec4 b = vec4(dot(B, rgbw), dot(D, rgbw), dot(H, rgbw), dot(F, rgbw));
                vec4 c = vec4(dot(C, rgbw), dot(A, rgbw), dot(G, rgbw), dot(I, rgbw));
                vec4 d = b.yzwx;
                vec4 e = vec4(dot(E, rgbw));
                vec4 f = b.wxyz;
                vec4 g = c.zwxy;
                vec4 h = b.zwxy;
                vec4 i = c.wxyz;

                vec4 i4 = vec4(dot(I4, rgbw), dot(C1, rgbw), dot(A0, rgbw), dot(G5, rgbw));
                vec4 i5 = vec4(dot(I5, rgbw), dot(C4, rgbw), dot(A1, rgbw), dot(G0, rgbw));
                vec4 h5 = vec4(dot(H5, rgbw), dot(F4, rgbw), dot(B1, rgbw), dot(D0, rgbw));
                vec4 f4 = h5.yzwx;

                // The lines below which each of the three slopes interpolates.
                vec4 fx   = Ao * fp.y + Bo * fp.x;
                vec4 fx_l = Ax * fp.y + Bx * fp.x;
                vec4 fx_u = Ay * fp.y + By * fp.x;

                vec4 irlv0 = diff(e, f) * diff(e, h);
                // Corner detection, the original's variant C.
                vec4 irlv1 = irlv0 * (neq(f, b) * neq(f, c) + neq(h, d) * neq(h, g)
                                      + eq(e, i) * (neq(f, f4) * neq(f, i4) + neq(h, h5) * neq(h, i5))
                                      + eq(e, g) + eq(e, c));
                vec4 irlv2l = diff(e, g) * diff(d, g);
                vec4 irlv2u = diff(e, c) * diff(b, c);

                vec4 fx45i = clamp((fx   + delta   - Co - Ci) / (2.0 * delta),   0.0, 1.0);
                vec4 fx45  = clamp((fx   + delta   - Co     ) / (2.0 * delta),   0.0, 1.0);
                vec4 fx30  = clamp((fx_l + delta_l - Cx     ) / (2.0 * delta_l), 0.0, 1.0);
                vec4 fx60  = clamp((fx_u + delta_u - Cy     ) / (2.0 * delta_u), 0.0, 1.0);

                vec4 wd1 = wd(e, c, g, i, h5, f4, h, f);
                vec4 wd2 = wd(h, d, i5, f, i4, b, e, i);

                vec4 edri  = step(wd1, wd2) * irlv0;
                vec4 edr   = step(wd1 + vec4(0.1), wd2) * step(vec4(0.5), irlv1);
                vec4 edr_l = step(XBR_LV2_COEFFICIENT * df(f, g), df(h, c)) * irlv2l * edr;
                vec4 edr_u = step(XBR_LV2_COEFFICIENT * df(h, c), df(f, g)) * irlv2u * edr;

                fx45  = edr   * fx45;
                fx30  = edr_l * fx30;
                fx60  = edr_u * fx60;
                fx45i = edri  * fx45i;

                vec4 px = step(df(e, f), df(e, h));

                vec4 maximos = max(max(fx30, fx60), max(fx45, fx45i));

                vec3 res1 = E;
                res1 = mix(res1, mix(H, F, px.x), maximos.x);
                res1 = mix(res1, mix(B, D, px.z), maximos.z);

                vec3 res2 = E;
                res2 = mix(res2, mix(F, B, px.y), maximos.y);
                res2 = mix(res2, mix(D, H, px.w), maximos.w);

                vec3 res = mix(res1, res2, step(c_df(E, res1), c_df(E, res2)));

                FRAGOUT = vec4(res, 1.0);
            }";

        // Shaders — phosphor persistence, the last stage before the screen: each frame is mixed
        // with the previous one, `uPersistence` (0..0.5) being the previous frame's share. Two
        // draws with the same program: the mix into the output, and — with the share at 0 — a
        // straight copy of the current frame into the history texture the next frame reads. It
        // is a two-frame average rather than an exponential decay on purpose: the point is the
        // ST's flicker tricks (pictures and demos alternating two palettes at 50 Hz for more
        // colours, sprites drawn every other frame for transparency), and only an average of
        // exactly the two frames shows those as a steady mix; a decaying tail leaves them
        // flickering at a third of their amplitude and drags a ghost behind everything moving.
        private const string PersistShaderBody = @"
            uniform sampler2D uTexture;   // this frame, the chain's output (unit 0)
            uniform sampler2D uPrevTex;   // the previous one (unit 1)
            uniform float uPersistence;

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec3 cur  = TEXFETCH(uTexture, uv).rgb;
                vec3 prev = TEXFETCH(uPrevTex, uv).rgb;
                FRAGOUT = vec4(mix(cur, prev, uPersistence), 1.0);
            }";

        private static string SignalFragmentDesktop  => ToDesktopFragment(SignalShaderBody);
        private static string SignalFragmentES       => ToEsFragment(SignalShaderBody);
        private static string XbrFragmentDesktop     => ToDesktopFragment(XbrShaderBody);
        private static string XbrFragmentES          => ToEsFragment(XbrShaderBody);
        private static string PersistFragmentDesktop => ToDesktopFragment(PersistShaderBody);
        private static string PersistFragmentES      => ToEsFragment(PersistShaderBody);

        // Shaders — anti-aliasing. The ST's 3D games rasterise their polygons and lines in
        // software, straight into the framebuffer, so by the time the picture reaches the GPU
        // there is no geometry left to multisample: MSAA has nothing to act on, TAA has no
        // sub-pixel jitter to accumulate, and SSAA would need the scene at a higher resolution
        // than the machine ever drew it. What *does* apply is image-space anti-aliasing — the
        // post-process family built precisely to reconstruct the edges of an already-aliased
        // picture — and the two below are the standard ones. Both run as the LAST pass of the
        // chain, over whatever the passes above produced and at that texture's own size: the
        // colorization and SuperEagle passes detect their patterns by comparing texels for
        // equality, and a filter that blends colours along every edge, run first, would leave
        // them nothing to find.
        //
        // FXAA 3.11 (Timothy Lottes, NVIDIA; the "quality" variant, search preset 39 — twelve
        // steps, since the picture is small and the pass runs once per emulated frame). One
        // pass: it measures the local luma contrast, decides whether the edge through the
        // texel is horizontal or vertical, walks along it in both directions to find its ends
        // and shifts the sample by the sub-texel offset the edge would have had. Its probes
        // sit on texel boundaries and read the average of the two texels through the bilinear
        // filter, so the source must be sampled LINEAR — which every input of this pass already
        // is. The sub-pixel low-pass (uSubpix, Config.FxaaSubpix) is the part that also softens
        // text and dithering, which is why its default sits below the original's 0.75. Luma is
        // Rec.601 rather than the
        // original's green-heavy shortcut: ST palettes are saturated primaries, and a pure blue
        // line over black has no green at all.
        private const string FxaaShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size

            uniform float uSubpix;        // Config.FxaaSubpix, 0..1

            const float EDGE_THRESHOLD = 0.166;
            const float EDGE_THRESHOLD_MIN = 0.0833;

            float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

            float lumaAt(vec2 uv, float dx, float dy) {
                return luma(TEXFETCH(uTexture, uv + vec2(dx, dy) * uTexel).rgb);
            }

            // Search steps of quality preset 39, in texels.
            float fxaaStep(int k) {
                return (k < 5) ? 1.0 : ((k == 5) ? 1.5 : ((k < 10) ? 2.0 : ((k == 10) ? 4.0 : 8.0)));
            }

            void main() {
                vec2 posM = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec3 rgbM = TEXFETCH(uTexture, posM).rgb;
                float lumaM = luma(rgbM);
                float lumaS = lumaAt(posM,  0.0,  1.0);
                float lumaE = lumaAt(posM,  1.0,  0.0);
                float lumaN = lumaAt(posM,  0.0, -1.0);
                float lumaW = lumaAt(posM, -1.0,  0.0);

                float maxSM = max(lumaS, lumaM);
                float minSM = min(lumaS, lumaM);
                float maxESM = max(lumaE, maxSM);
                float minESM = min(lumaE, minSM);
                float maxWN = max(lumaN, lumaW);
                float minWN = min(lumaN, lumaW);
                float rangeMax = max(maxWN, maxESM);
                float rangeMin = min(minWN, minESM);
                float rangeMaxScaled = rangeMax * EDGE_THRESHOLD;
                float range = rangeMax - rangeMin;
                float rangeMaxClamped = max(EDGE_THRESHOLD_MIN, rangeMaxScaled);

                if (range < rangeMaxClamped) {
                    FRAGOUT = vec4(rgbM, 1.0);
                    return;
                }

                float lumaNW = lumaAt(posM, -1.0, -1.0);
                float lumaSE = lumaAt(posM,  1.0,  1.0);
                float lumaNE = lumaAt(posM,  1.0, -1.0);
                float lumaSW = lumaAt(posM, -1.0,  1.0);

                float lumaNS = lumaN + lumaS;
                float lumaWE = lumaW + lumaE;
                float subpixRcpRange = 1.0 / range;
                float subpixNSWE = lumaNS + lumaWE;
                float edgeHorz1 = (-2.0 * lumaM) + lumaNS;
                float edgeVert1 = (-2.0 * lumaM) + lumaWE;

                float lumaNESE = lumaNE + lumaSE;
                float lumaNWNE = lumaNW + lumaNE;
                float edgeHorz2 = (-2.0 * lumaE) + lumaNESE;
                float edgeVert2 = (-2.0 * lumaN) + lumaNWNE;

                float lumaNWSW = lumaNW + lumaSW;
                float lumaSWSE = lumaSW + lumaSE;
                float edgeHorz4 = (abs(edgeHorz1) * 2.0) + abs(edgeHorz2);
                float edgeVert4 = (abs(edgeVert1) * 2.0) + abs(edgeVert2);
                float edgeHorz3 = (-2.0 * lumaW) + lumaNWSW;
                float edgeVert3 = (-2.0 * lumaS) + lumaSWSE;
                float edgeHorz = abs(edgeHorz3) + edgeHorz4;
                float edgeVert = abs(edgeVert3) + edgeVert4;

                float subpixNWSWNESE = lumaNWSW + lumaNESE;
                float lengthSign = uTexel.x;
                bool horzSpan = edgeHorz >= edgeVert;
                float subpixA = subpixNSWE * 2.0 + subpixNWSWNESE;

                if (!horzSpan) lumaN = lumaW;
                if (!horzSpan) lumaS = lumaE;
                if (horzSpan) lengthSign = uTexel.y;
                float subpixB = (subpixA * (1.0 / 12.0)) - lumaM;

                float gradientN = lumaN - lumaM;
                float gradientS = lumaS - lumaM;
                float lumaNN = lumaN + lumaM;
                float lumaSS = lumaS + lumaM;
                bool pairN = abs(gradientN) >= abs(gradientS);
                float gradient = max(abs(gradientN), abs(gradientS));
                if (pairN) lengthSign = -lengthSign;
                float subpixC = clamp(abs(subpixB) * subpixRcpRange, 0.0, 1.0);

                vec2 posB = posM;
                vec2 offNP;
                offNP.x = (!horzSpan) ? 0.0 : uTexel.x;
                offNP.y = ( horzSpan) ? 0.0 : uTexel.y;
                if (!horzSpan) posB.x += lengthSign * 0.5;
                if ( horzSpan) posB.y += lengthSign * 0.5;

                vec2 posN = posB - offNP * fxaaStep(0);
                vec2 posP = posB + offNP * fxaaStep(0);
                float subpixD = ((-2.0) * subpixC) + 3.0;
                float lumaEndN = luma(TEXFETCH(uTexture, posN).rgb);
                float subpixE = subpixC * subpixC;
                float lumaEndP = luma(TEXFETCH(uTexture, posP).rgb);

                if (!pairN) lumaNN = lumaSS;
                float gradientScaled = gradient * (1.0 / 4.0);
                float lumaMM = lumaM - lumaNN * 0.5;
                float subpixF = subpixD * subpixE;
                bool lumaMLTZero = lumaMM < 0.0;

                lumaEndN -= lumaNN * 0.5;
                lumaEndP -= lumaNN * 0.5;
                bool doneN = abs(lumaEndN) >= gradientScaled;
                bool doneP = abs(lumaEndP) >= gradientScaled;
                if (!doneN) posN -= offNP * fxaaStep(1);
                if (!doneP) posP += offNP * fxaaStep(1);
                bool doneNP = (!doneN) || (!doneP);

                for (int k = 2; k < 12; k++) {
                    if (!doneNP) break;
                    if (!doneN) lumaEndN = luma(TEXFETCH(uTexture, posN).rgb) - lumaNN * 0.5;
                    if (!doneP) lumaEndP = luma(TEXFETCH(uTexture, posP).rgb) - lumaNN * 0.5;
                    doneN = abs(lumaEndN) >= gradientScaled;
                    doneP = abs(lumaEndP) >= gradientScaled;
                    if (!doneN) posN -= offNP * fxaaStep(k);
                    if (!doneP) posP += offNP * fxaaStep(k);
                    doneNP = (!doneN) || (!doneP);
                }

                float dstN = posM.x - posN.x;
                float dstP = posP.x - posM.x;
                if (!horzSpan) dstN = posM.y - posN.y;
                if (!horzSpan) dstP = posP.y - posM.y;

                bool goodSpanN = (lumaEndN < 0.0) != lumaMLTZero;
                float spanLength = (dstP + dstN);
                bool goodSpanP = (lumaEndP < 0.0) != lumaMLTZero;
                float spanLengthRcp = 1.0 / spanLength;

                bool directionN = dstN < dstP;
                float dst = min(dstN, dstP);
                bool goodSpan = directionN ? goodSpanN : goodSpanP;
                float subpixG = subpixF * subpixF;
                float pixelOffset = (dst * (-spanLengthRcp)) + 0.5;
                float subpixH = subpixG * uSubpix;

                float pixelOffsetGood = goodSpan ? pixelOffset : 0.0;
                float pixelOffsetSubpix = max(pixelOffsetGood, subpixH);
                if (!horzSpan) posM.x += pixelOffsetSubpix * lengthSign;
                if ( horzSpan) posM.y += pixelOffsetSubpix * lengthSign;

                FRAGOUT = vec4(TEXFETCH(uTexture, posM).rgb, 1.0);
            }";

        private static string FxaaFragmentDesktop => ToDesktopFragment(FxaaShaderBody);
        private static string FxaaFragmentES      => ToEsFragment(FxaaShaderBody);

        // SMAA 1x (Jorge Jimenez, Jose I. Echevarria, Belen Masia, Fernando Navarro, Diego
        // Gutierrez — "SMAA: Enhanced Subpixel Morphological Antialiasing", 2012; MIT licence,
        // copyright (C) 2013 the authors, reference implementation at github.com/iryoku/smaa).
        // Ported pass by pass from the reference SMAA.hlsl with its ULTRA search depth (32
        // orthogonal / 16 diagonal steps, 25% corner rounding); the edge threshold is
        // Config.SmaaThreshold (default 0.1, the reference's HIGH preset);
        // GLSL ES 1.00 has no `while` and no `round`, so the searches are `for` loops with
        // constant bounds and rnd() is floor(x + 0.5) — the values it is applied to are never
        // exactly halfway.
        //
        // Three passes. (1) Edge detection, colour-based: an edge is marked on the left and top
        // sides of a texel where any channel differs from the neighbour by more than the
        // threshold, thinned by local contrast adaptation. Colour rather than luma for the same
        // reason as FXAA's luma above — a saturated primary against black can have almost no
        // luma difference. (2) Blending weights, the morphological part: for every edge texel
        // it walks the edge to both ends (two texels per fetch, exploiting bilinear filtering on
        // the edges texture — which must therefore be sampled LINEAR), reads which way the
        // crossing edges at the ends go, classifies the shape (L, Z, U, or a diagonal) and looks
        // the coverage of the reconstructed silhouette up in the precomputed AREA texture,
        // indexed by the two distances and the two crossings; the SEARCH texture is the lookup
        // that tells how many texels the last bilinear fetch of a search really covered. Both
        // tables are the reference ones, shipped as Resources/SMAA_*.bin. (3) Neighbourhood
        // blending: each texel is mixed with the neighbour across its edge by those weights,
        // again through one bilinear fetch of the colour texture at a fractional offset.
        //
        // Orientation: the reference was written for Direct3D texture coordinates, with v
        // growing DOWN the picture, and that is the convention of every texture in this chain
        // (see the flip at the top of each main()), so its offsets are used unchanged: `top`
        // is -v. The lookup tables are uploaded with their first row at t = 0, which is the
        // same convention, so their scale/bias arithmetic is unchanged too.
        private const string SmaaEdgeShaderBody = @"
            uniform sampler2D uTexture;
            uniform vec2 uTexel;          // 1 / source size

            uniform float uThreshold;     // Config.SmaaThreshold, 0.01..0.5

            const float SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR = 2.0;

            vec3 colorAt(vec2 uv, float dx, float dy) {
                return TEXFETCH(uTexture, uv + vec2(dx, dy) * uTexel).rgb;
            }

            float maxDiff(vec3 a, vec3 b) {
                vec3 t = abs(a - b);
                return max(max(t.r, t.g), t.b);
            }

            bool same(vec3 a, vec3 b) {
                return abs(a.r - b.r) + abs(a.g - b.g) + abs(a.b - b.b) < 0.005;
            }

            void main() {
                vec2 uv = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec3 C = colorAt(uv, 0.0, 0.0);

                // Is this part of the picture pixel-doubled (low resolution: 832 columns for
                // 416 half-cycles)? Same test as the dithering shader — every column pairs up
                // with a neighbour on a single phase over three rows — and carried to the weight
                // pass in the blue channel, whose corner detection needs to know how wide one ST
                // pixel is (see detectVerticalCornerPattern). A flat area answers yes, which
                // costs nothing: the flag only matters where there is an edge.
                vec3 row[27];
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 9; i++)
                        row[j * 9 + i] = colorAt(uv, float(i - 4), float(j - 1));

                bool phaseA = true;
                bool phaseB = true;
                for (int j = 0; j < 3; j++) {
                    for (int k = 0; k < 4; k++) {
                        if (!same(row[j * 9 + 2 * k],     row[j * 9 + 2 * k + 1])) phaseA = false;
                        if (!same(row[j * 9 + 2 * k + 1], row[j * 9 + 2 * k + 2])) phaseB = false;
                    }
                }
                float doubled = (phaseA || phaseB) ? 1.0 : 0.0;

                vec4 delta;
                delta.x = maxDiff(C, colorAt(uv, -1.0,  0.0));
                delta.y = maxDiff(C, colorAt(uv,  0.0, -1.0));
                vec2 edges = step(vec2(uThreshold), delta.xy);

                if (dot(edges, vec2(1.0)) == 0.0) {
                    FRAGOUT = vec4(0.0, 0.0, doubled, 0.0);
                    return;
                }

                delta.z = maxDiff(C, colorAt(uv, 1.0, 0.0));
                delta.w = maxDiff(C, colorAt(uv, 0.0, 1.0));
                vec2 maxDelta = max(delta.xy, delta.zw);

                delta.z = maxDiff(C, colorAt(uv, -2.0,  0.0));
                delta.w = maxDiff(C, colorAt(uv,  0.0, -2.0));
                maxDelta = max(maxDelta.xy, delta.zw);
                float finalDelta = max(maxDelta.x, maxDelta.y);

                // Local contrast adaptation: an edge only counts if it is not dwarfed by a
                // stronger one right next to it.
                edges *= step(vec2(finalDelta), SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta.xy);

                FRAGOUT = vec4(edges, doubled, 1.0);
            }";

        private const string SmaaWeightShaderBody = @"
            uniform sampler2D uTexture;   // edges, from the pass above (unit 0, LINEAR)
            uniform sampler2D uAreaTex;   // unit 1, LINEAR
            uniform sampler2D uSearchTex; // unit 2, NEAREST
            uniform vec2 uTexel;          // 1 / source size
            uniform vec2 uSourceSize;     // source size, in texels

            const float SMAA_CORNER_ROUNDING_NORM = 0.25;
            const float SMAA_AREATEX_MAX_DISTANCE = 16.0;
            const float SMAA_AREATEX_MAX_DISTANCE_DIAG = 20.0;
            const vec2  SMAA_AREATEX_PIXEL_SIZE = vec2(1.0 / 160.0, 1.0 / 560.0);
            const vec2  SMAA_SEARCHTEX_SIZE = vec2(66.0, 33.0);
            const vec2  SMAA_SEARCHTEX_PACKED_SIZE = vec2(64.0, 16.0);

            vec2 rnd2(vec2 v) { return floor(v + 0.5); }
            vec4 rnd4(vec4 v) { return floor(v + 0.5); }

            vec4 edgesAt(vec2 uv) { return TEXFETCH(uTexture, uv); }
            vec4 edgesOff(vec2 uv, float dx, float dy) { return TEXFETCH(uTexture, uv + vec2(dx, dy) * uTexel); }

            // ---- diagonal patterns

            // A bilinear fetch at a 0.25 offset packs two edge flags into one value; this
            // unpacks them (the reference's SMAADecodeDiagBilinearAccess).
            vec2 decodeDiag2(vec2 e) {
                e.r = e.r * abs(5.0 * e.r - 5.0 * 0.75);
                return rnd2(e);
            }

            vec4 decodeDiag4(vec4 e) {
                e.rb = e.rb * abs(5.0 * e.rb - 5.0 * 0.75);
                return rnd4(e);
            }

            vec2 searchDiag1(vec2 texcoord, vec2 dir, out vec2 e) {
                vec4 coord = vec4(texcoord, -1.0, 1.0);
                vec3 t = vec3(uTexel, 1.0);
                e = vec2(0.0);
                for (int i = 0; i < 16; i++) {
                    if (!(coord.z < 15.0 && coord.w > 0.9)) break;
                    coord.xyz = t * vec3(dir, 1.0) + coord.xyz;
                    e = edgesAt(coord.xy).rg;
                    coord.w = dot(e, vec2(0.5));
                }
                return coord.zw;
            }

            vec2 searchDiag2(vec2 texcoord, vec2 dir, out vec2 e) {
                vec4 coord = vec4(texcoord, -1.0, 1.0);
                coord.x += 0.25 * uTexel.x;
                vec3 t = vec3(uTexel, 1.0);
                e = vec2(0.0);
                for (int i = 0; i < 16; i++) {
                    if (!(coord.z < 15.0 && coord.w > 0.9)) break;
                    coord.xyz = t * vec3(dir, 1.0) + coord.xyz;
                    e = decodeDiag2(edgesAt(coord.xy).rg);
                    coord.w = dot(e, vec2(0.5));
                }
                return coord.zw;
            }

            vec2 areaDiag(vec2 dist, vec2 e) {
                vec2 texcoord = vec2(SMAA_AREATEX_MAX_DISTANCE_DIAG) * e + dist;
                texcoord = SMAA_AREATEX_PIXEL_SIZE * texcoord + 0.5 * SMAA_AREATEX_PIXEL_SIZE;
                texcoord.x += 0.5;   // diagonal areas live in the right half of the table
                return TEXFETCH(uAreaTex, texcoord).rg;
            }

            vec2 calculateDiagWeights(vec2 texcoord, vec2 e) {
                vec2 weights = vec2(0.0);
                vec4 d;
                vec2 end;

                if (e.r > 0.0) {
                    d.xz = searchDiag1(texcoord, vec2(-1.0, 1.0), end);
                    d.x += float(end.y > 0.9);
                } else
                    d.xz = vec2(0.0);
                d.yw = searchDiag1(texcoord, vec2(1.0, -1.0), end);

                if (d.x + d.y > 2.0) {
                    vec4 coords = vec4(-d.x + 0.25, d.x, d.y, -d.y - 0.25) * uTexel.xyxy + texcoord.xyxy;
                    vec4 c;
                    c.xy = edgesOff(coords.xy, -1.0, 0.0).rg;
                    c.zw = edgesOff(coords.zw,  1.0, 0.0).rg;
                    c.yxwz = decodeDiag4(c.xyzw);

                    vec2 cc = vec2(2.0) * c.xz + c.yw;
                    // Drop the crossing edge where the search ran out of steps instead of
                    // finding the end of the line.
                    if (d.z >= 0.9) cc.x = 0.0;
                    if (d.w >= 0.9) cc.y = 0.0;

                    weights += areaDiag(d.xy, cc);
                }

                d.xz = searchDiag2(texcoord, vec2(-1.0, -1.0), end);
                if (edgesOff(texcoord, 1.0, 0.0).r > 0.0) {
                    d.yw = searchDiag2(texcoord, vec2(1.0, 1.0), end);
                    d.y += float(end.y > 0.9);
                } else
                    d.yw = vec2(0.0);

                if (d.x + d.y > 2.0) {
                    vec4 coords = vec4(-d.x, -d.x, d.y, d.y) * uTexel.xyxy + texcoord.xyxy;
                    vec4 c;
                    c.x  = edgesOff(coords.xy, -1.0,  0.0).g;
                    c.y  = edgesOff(coords.xy,  0.0, -1.0).r;
                    c.zw = edgesOff(coords.zw,  1.0,  0.0).gr;
                    vec2 cc = vec2(2.0) * c.xz + c.yw;
                    if (d.z >= 0.9) cc.x = 0.0;
                    if (d.w >= 0.9) cc.y = 0.0;

                    weights += areaDiag(d.xy, cc).gr;
                }

                return weights;
            }

            // ---- horizontal / vertical searches

            // How many texels the last bilinear fetch of a search really covered (0, 1 or 2),
            // read off the search table.
            float searchLength(vec2 e, float offset) {
                vec2 scale = SMAA_SEARCHTEX_SIZE * vec2(0.5, -1.0);
                vec2 bias  = SMAA_SEARCHTEX_SIZE * vec2(offset, 1.0);
                scale += vec2(-1.0,  1.0);
                bias  += vec2( 0.5, -0.5);
                scale *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;
                bias  *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;
                return TEXFETCH(uSearchTex, scale * e + bias).r;
            }

            // The texcoord handed in was offset by (-0.25, -0.125) so that each fetch reads
            // two edge texels at once through the bilinear filter; a value under 0.8281 means
            // one of the two is off, a red component means a crossing edge broke the line.
            float searchXLeft(vec2 texcoord, float end) {
                vec2 e = vec2(0.0, 1.0);
                for (int i = 0; i < 33; i++) {
                    if (!(texcoord.x > end && e.g > 0.8281 && e.r == 0.0)) break;
                    e = edgesAt(texcoord).rg;
                    texcoord -= vec2(2.0, 0.0) * uTexel;
                }
                float offset = -(255.0 / 127.0) * searchLength(e, 0.0) + 3.25;
                return uTexel.x * offset + texcoord.x;
            }

            float searchXRight(vec2 texcoord, float end) {
                vec2 e = vec2(0.0, 1.0);
                for (int i = 0; i < 33; i++) {
                    if (!(texcoord.x < end && e.g > 0.8281 && e.r == 0.0)) break;
                    e = edgesAt(texcoord).rg;
                    texcoord += vec2(2.0, 0.0) * uTexel;
                }
                float offset = -(255.0 / 127.0) * searchLength(e, 0.5) + 3.25;
                return -uTexel.x * offset + texcoord.x;
            }

            float searchYUp(vec2 texcoord, float end) {
                vec2 e = vec2(1.0, 0.0);
                for (int i = 0; i < 33; i++) {
                    if (!(texcoord.y > end && e.r > 0.8281 && e.g == 0.0)) break;
                    e = edgesAt(texcoord).rg;
                    texcoord -= vec2(0.0, 2.0) * uTexel;
                }
                float offset = -(255.0 / 127.0) * searchLength(e.gr, 0.0) + 3.25;
                return uTexel.y * offset + texcoord.y;
            }

            float searchYDown(vec2 texcoord, float end) {
                vec2 e = vec2(1.0, 0.0);
                for (int i = 0; i < 33; i++) {
                    if (!(texcoord.y < end && e.r > 0.8281 && e.g == 0.0)) break;
                    e = edgesAt(texcoord).rg;
                    texcoord += vec2(0.0, 2.0) * uTexel;
                }
                float offset = -(255.0 / 127.0) * searchLength(e.gr, 0.5) + 3.25;
                return -uTexel.y * offset + texcoord.y;
            }

            // Coverage of the reconstructed edge on each side of this texel, given the two
            // distances (square-rooted: the table is compressed quadratically) and the two
            // crossing edges. Rounding the crossings keeps bilinear filtering from mixing rows.
            vec2 area(vec2 dist, float e1, float e2) {
                vec2 texcoord = vec2(SMAA_AREATEX_MAX_DISTANCE) * rnd2(4.0 * vec2(e1, e2)) + dist;
                texcoord = SMAA_AREATEX_PIXEL_SIZE * texcoord + 0.5 * SMAA_AREATEX_PIXEL_SIZE;
                return TEXFETCH(uAreaTex, texcoord).rg;
            }

            // ---- corners: a sharp corner is not a staircase, so the blending is reduced
            // where the edge turns back on itself.

            vec2 detectHorizontalCornerPattern(vec2 weights, vec4 texcoord, vec2 d) {
                vec2 leftRight = step(d.xy, d.yx);
                vec2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;
                rounding /= leftRight.x + leftRight.y;

                vec2 factor = vec2(1.0);
                factor.x -= rounding.x * edgesOff(texcoord.xy, 0.0,  1.0).r;
                factor.x -= rounding.y * edgesOff(texcoord.zw, 1.0,  1.0).r;
                factor.y -= rounding.x * edgesOff(texcoord.xy, 0.0, -2.0).r;
                factor.y -= rounding.y * edgesOff(texcoord.zw, 1.0, -2.0).r;

                return weights * clamp(factor, 0.0, 1.0);
            }

            // A corner is a crossing edge that runs on past the step: the reference looks one
            // texel beyond, which is one pixel in every picture it was written for. Here a
            // low-resolution line is pixel-doubled, so its ordinary staircase steps are two
            // texels wide and read as corners — on such a picture the blending of every
            // vertical edge came out at a quarter of its strength — hence `doubled`, the flag
            // the edge pass computed, pushes the look-ahead one texel further. The horizontal
            // case is unaffected: rows are never doubled.
            vec2 detectVerticalCornerPattern(vec2 weights, vec4 texcoord, vec2 d, float doubled) {
                vec2 leftRight = step(d.xy, d.yx);
                vec2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;
                rounding /= leftRight.x + leftRight.y;

                float right = 1.0 + doubled;
                float left  = -2.0 - doubled;

                vec2 factor = vec2(1.0);
                factor.x -= rounding.x * edgesOff(texcoord.xy, right, 0.0).g;
                factor.x -= rounding.y * edgesOff(texcoord.zw, right, 1.0).g;
                factor.y -= rounding.x * edgesOff(texcoord.xy, left,  0.0).g;
                factor.y -= rounding.y * edgesOff(texcoord.zw, left,  1.0).g;

                return weights * clamp(factor, 0.0, 1.0);
            }

            void main() {
                vec2 texcoord = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec2 pixcoord = texcoord * uSourceSize;

                // The offsets the reference computes in its vertex shader: the search start
                // points (bilinear trick, see searchXLeft) and the search limits.
                vec4 offset0 = vec4(-0.25, -0.125,  1.25, -0.125) * uTexel.xyxy + texcoord.xyxy;
                vec4 offset1 = vec4(-0.125, -0.25, -0.125,  1.25) * uTexel.xyxy + texcoord.xyxy;
                vec4 offset2 = vec4(-2.0, 2.0, -2.0, 2.0) * 32.0 * uTexel.xxyy + vec4(offset0.xz, offset1.yw);

                vec4 weights = vec4(0.0);
                vec3 e3 = edgesAt(texcoord).rgb;
                vec2 e = e3.rg;

                if (e.g > 0.0) {   // edge at the top
                    // Diagonals have both a top and a left edge, so one look is enough — and
                    // they take priority: a diagonal found here skips the orthogonal search.
                    weights.rg = calculateDiagWeights(texcoord, e);

                    if (weights.r == -weights.g) {
                        vec2 d;
                        vec3 coords;

                        coords.x = searchXLeft(offset0.xy, offset2.x);
                        coords.y = offset1.y;
                        d.x = coords.x;

                        // The crossing edges at the left end, two at a time through the
                        // bilinear filter (the -0.25 offset in y tells them apart).
                        float e1 = edgesAt(coords.xy).r;

                        coords.z = searchXRight(offset0.zw, offset2.y);
                        d.y = coords.z;

                        // Distances in texels.
                        d = abs(rnd2(uSourceSize.xx * d - pixcoord.xx));
                        vec2 sqrt_d = sqrt(d);

                        float e2 = edgesOff(coords.zy, 1.0, 0.0).r;

                        weights.rg = area(sqrt_d, e1, e2);

                        coords.y = texcoord.y;
                        weights.rg = detectHorizontalCornerPattern(weights.rg, coords.xyzy, d);
                    } else
                        e.r = 0.0;   // skip the vertical processing
                }

                if (e.r > 0.0) {   // edge at the left
                    vec2 d;
                    vec3 coords;

                    coords.y = searchYUp(offset1.xy, offset2.z);
                    coords.x = offset0.x;
                    d.x = coords.y;

                    float e1 = edgesAt(coords.xy).g;

                    coords.z = searchYDown(offset1.zw, offset2.w);
                    d.y = coords.z;

                    d = abs(rnd2(uSourceSize.yy * d - pixcoord.yy));
                    vec2 sqrt_d = sqrt(d);

                    float e2 = edgesOff(coords.xz, 0.0, 1.0).g;

                    weights.ba = area(sqrt_d, e1, e2);

                    coords.x = texcoord.x;
                    weights.ba = detectVerticalCornerPattern(weights.ba, coords.xyxz, d, e3.b);
                }

                FRAGOUT = weights;
            }";

        private const string SmaaBlendShaderBody = @"
            uniform sampler2D uTexture;   // the colour source of the chain (unit 0, LINEAR)
            uniform sampler2D uBlendTex;  // weights, from the pass above (unit 1)
            uniform vec2 uTexel;          // 1 / source size

            void main() {
                vec2 texcoord = vec2(V_TEXCOORD.x, 1.0 - V_TEXCOORD.y);
                vec4 offset = vec4(1.0, 0.0, 0.0, 1.0) * uTexel.xyxy + texcoord.xyxy;

                // The four weights that concern this texel: its own bottom and left ones, the
                // right neighbour's and the bottom neighbour's.
                vec4 a;
                a.x  = TEXFETCH(uBlendTex, offset.xy).a;
                a.y  = TEXFETCH(uBlendTex, offset.zw).g;
                a.wz = TEXFETCH(uBlendTex, texcoord).xz;

                if (dot(a, vec4(1.0)) < 1e-5) {
                    FRAGOUT = vec4(TEXFETCH(uTexture, texcoord).rgb, 1.0);
                    return;
                }

                // Blend along the stronger axis only; the fractional offset makes the bilinear
                // filter do the mix with the neighbour across the edge.
                bool h = max(a.x, a.z) > max(a.y, a.w);
                vec4 blendingOffset = vec4(0.0, a.y, 0.0, a.w);
                vec2 blendingWeight = a.yw;
                if (h) {
                    blendingOffset = vec4(a.x, 0.0, a.z, 0.0);
                    blendingWeight = a.xz;
                }
                blendingWeight /= dot(blendingWeight, vec2(1.0));

                vec4 blendingCoord = blendingOffset * vec4(uTexel.xy, -uTexel.xy) + texcoord.xyxy;

                vec3 color = blendingWeight.x * TEXFETCH(uTexture, blendingCoord.xy).rgb;
                color += blendingWeight.y * TEXFETCH(uTexture, blendingCoord.zw).rgb;

                FRAGOUT = vec4(color, 1.0);
            }";

        private static string SmaaEdgeFragmentDesktop   => ToDesktopFragment(SmaaEdgeShaderBody);
        private static string SmaaEdgeFragmentES        => ToEsFragment(SmaaEdgeShaderBody);
        private static string SmaaWeightFragmentDesktop => ToDesktopFragment(SmaaWeightShaderBody);
        private static string SmaaWeightFragmentES      => ToEsFragment(SmaaWeightShaderBody);
        private static string SmaaBlendFragmentDesktop  => ToDesktopFragment(SmaaBlendShaderBody);
        private static string SmaaBlendFragmentES       => ToEsFragment(SmaaBlendShaderBody);

        // The same two spellings the passes above write out by hand.
        private static string ToDesktopFragment(string body) =>
            "in vec2 v_texCoord;\nout vec4 fragColor;\n" +
            body.Replace("V_TEXCOORD", "v_texCoord")
                .Replace("TEXFETCH", "texture")
                .Replace("FRAGOUT", "fragColor");

        private static string ToEsFragment(string body) =>
            "#version 100\nprecision highp float;\nvarying vec2 v_texCoord;\n" +
            body.Replace("V_TEXCOORD", "v_texCoord")
                .Replace("TEXFETCH", "texture2D")
                .Replace("FRAGOUT", "gl_FragColor");

        // =====================================================================

        /// <summary>
        /// A linked GL program together with its uniform locations. A location of -1 means the
        /// uniform is not present in that program (the effect uniforms in the plain shader, or
        /// anything the driver optimised away), which the render path already treats as "skip".
        /// </summary>
        private sealed class ShaderProgram
        {
            public uint Id;
            public bool Valid;

            public int Texture = -1, SourceSize = -1, OutputSize = -1, Time = -1;
            public int Curvature = -1, Vignette = -1, Scanline = -1, ChromAb = -1;
            public int Bloom = -1, Mask = -1, Noise = -1;
            public int TexMin = -1, TexMax = -1;
            public int Texel = -1;      // dithering / gradient passes only
            public int Dir   = -1;      // gradient pass only
            public int AreaTex   = -1;  // SMAA blending-weight pass only (unit 1)
            public int SearchTex = -1;  // SMAA blending-weight pass only (unit 2)
            public int BlendTex  = -1;  // SMAA neighbourhood-blending pass only (unit 1)
            public int Subpix    = -1;  // FXAA only
            public int Threshold = -1;  // SMAA edge pass only
            public int TexSize     = -1;  // final pass: size of the texture it samples
            public int Sharp       = -1;  // final pass: sharp-bilinear on/off
            public int MaskType    = -1;  // CRT only
            public int MaskScale   = -1;  // CRT only
            public int ChromaRadius = -1; // video signal pass only
            public int LumaSoftness = -1; // video signal pass only
            public int Artefacts    = -1; // video signal pass only
            public int Frame       = -1;  // video signal pass only
            public int PrevTex     = -1;  // persistence pass only (unit 1)
            public int Persistence = -1;  // persistence pass only
        }

        private GL _gl;
        private uint _textureId;
        private ShaderProgram _crtProgram;
        private ShaderProgram _plainProgram;

        // Dithering colorization: two passes, each with its program, the texture it renders
        // into and the FBO that targets it. The chain is raw -> dither -> gradient(V) ->
        // gradient(H) -> dither, so the result always ends up in _ditherTexture and the two
        // targets ping-pong (no pass ever reads the texture it is writing). _ditherGeom tracks
        // which video geometry the targets were built for (also the "already tried" marker: a
        // failed build is not retried until the geometry changes), and _ditherDirty forces the
        // chain to run once even without a new emulated frame — after a resize, and when the
        // user switches the filter on.
        private ShaderProgram _ditherProgram;
        private ShaderProgram _gradientProgram;
        private uint _ditherTexture;
        private uint _ditherFbo;
        private uint _gradientTexture;
        private uint _gradientFbo;
        private bool _ditherFboValid;
        private bool _gradientFboValid;
        private bool _ditherDirty = true;
        private bool _ditherEnabled;
        private int _ditherGeom = -1;

        // Edge smoothing (SuperEagle or xBR): one more pass, on the end of whatever the chain
        // above produced, into a target twice the size of the framebuffer — so this one is not
        // part of the ping-pong and never shares a texture with anything. The two filters share
        // the target and the bookkeeping; which program runs is decided per frame. _eagleGeom is
        // both "built for this geometry" and "already tried and failed", and _eagleDirty forces
        // the pass once when its input changed without a new emulated frame.
        private ShaderProgram _eagleProgram;
        private ShaderProgram _xbrProgram;
        private uint _eagleTexture;
        private uint _eagleFbo;
        private bool _eagleFboValid;
        private bool _eagleDirty = true;
        private Config.ConfigOptions.EdgeSmoothings _eagleMode = Config.ConfigOptions.EdgeSmoothings.None;
        private int _eagleGeom = -1;

        // Video signal (composite / RF): the first pass of the chain, at the source resolution.
        // _frameCounter is what moves its dot crawl: it counts published frames, not redraws.
        private ShaderProgram _signalProgram;
        private uint _signalTexture;
        private uint _signalFbo;
        private bool _signalFboValid;
        private bool _signalDirty = true;
        private Config.ConfigOptions.VideoSignals _signalMode = Config.ConfigOptions.VideoSignals.RGB;
        private int _signalGeom = -1;
        private long _frameCounter;

        // Phosphor persistence: the last stage, keyed on the scale like the anti-aliasing
        // targets, with a history texture that holds the previous frame.
        private ShaderProgram _persistProgram;
        private uint _persistTexture;
        private uint _persistFbo;
        private uint _persistPrevTexture;
        private uint _persistPrevFbo;
        private bool _persistFboValid;
        private bool _persistHistoryValid;
        private bool _persistDirty = true;
        private bool _persistEnabled;
        private int _persistGeom = -1;
        private int _persistScale;
        // Anti-aliasing (FXAA or SMAA): the fourth stage, over whatever the stages above
        // produced — the raw frame, the signal-filtered one, the colorized one or the 2x
        // smoothed one — into a target of that same size. FXAA is one program and one draw;
        // SMAA is three (edge detection, blending weights, neighbourhood blending) through two
        // intermediate targets plus the two lookup tables it ships with. Same bookkeeping as
        // the stages before it, with one more dimension: the input is 2x while edge smoothing
        // is on and 1x otherwise, so the targets are keyed on the scale as well as the geometry.
        private ShaderProgram _fxaaProgram;
        private ShaderProgram _smaaEdgeProgram;
        private ShaderProgram _smaaWeightProgram;
        private ShaderProgram _smaaBlendProgram;
        private uint _smaaAreaTexture;
        private uint _smaaSearchTexture;
        private bool _smaaLookupsValid;
        private uint _aaTexture;
        private uint _aaFbo;
        private uint _aaEdgesTexture;
        private uint _aaEdgesFbo;
        private uint _aaWeightsTexture;
        private uint _aaWeightsFbo;
        private bool _aaFboValid;
        private bool _aaSmaaFboValid;
        private bool _aaDirty = true;
        private Config.ConfigOptions.AntiAliasingModes _aaMode = Config.ConfigOptions.AntiAliasingModes.None;
        private int _aaGeom = -1;
        private int _aaScale;

        private uint _vao;
        private uint _vbo;
        private bool _hasVao;
        private bool _firstRender = true;
        private double _lastLoggedScaling = -1;

        // The TopLevel this control hangs from, cached on attach: it is the only object that
        // reports the DPI scale. Do *not* go through VisualRoot — see ScreenScaling below.
        private Avalonia.Controls.TopLevel _topLevel;

        private readonly Stopwatch _timer = new Stopwatch();

        // Atari screen size (the active video geometry). Runtime, not compile-time: a colour
        // monitor is 832x288 and a monochrome one 640x400, and the user can switch between them
        // (through a reset). The texture is resized to match whenever VideoTiming's geometry
        // generation changes; see EnsureTextureGeometry.
        private int _srcW = VideoTiming.BUFFER_WIDTH;
        private int _srcH = VideoTiming.BUFFER_HEIGHT;
        private int _geomGen = -1;   // -1 forces the first render to size the texture

        // Pixel aspect ratio of the ST on an original 4:3 PAL monitor: the tube shows
        // ~52 µs of active line over ~288 visible lines, so the 416 low-res pixels (8 MHz)
        // that fit in 52 µs span 288 * 4/3 = 384 line-height units -> each pixel is
        // 384/416 = 12/13 as wide as it is tall (slightly narrower than square).
        public const double PIXEL_ASPECT = 12.0 / 13.0;

        /// <summary>Aspect ratio of the picture this control shows. On a colour monitor it is the
        /// full framebuffer (display + borders) or the 640x400 crop when borders are hidden,
        /// corrected by the pixel aspect of the original 4:3 monitor. On a monochrome monitor it
        /// is the native 640x400 with square pixels.
        /// <para>It belongs here rather than to the window because it is what the letterbox in
        /// <see cref="OnOpenGlRender"/> fits the picture to; the window's own resize logic
        /// (MainWindow.EnforceAspectRatio) reads the same value so both agree.</para></summary>
        public static double DisplayAspectRatio =>
            VideoTiming.Mono
                ? (double)VideoTiming.BUFFER_WIDTH / VideoTiming.BUFFER_HEIGHT
                : PIXEL_ASPECT * (Config.ConfigOptions.RunninConfig.ShowBorders
                    ? (double)VideoTiming.BUFFER_WIDTH / (VideoTiming.BUFFER_HEIGHT * 2)
                    : (double)VideoTiming.DISPLAY_TEX_WIDTH / (VideoTiming.DISPLAY_TEX_HEIGHT * 2));

        // Sequence number of the frame currently in the texture. The GL thread renders free-running
        // and normally beats the 50 Hz emulation, so this is what stops it from re-uploading the
        // same ~940 KB several times per emulated frame.
        private long _frameSeq;

        private bool CheckShader(uint shader, string name)
        {
            _gl.GetShader(shader, GLEnum.CompileStatus, out int status);
            if (status == 0)
            {
                string log = _gl.GetShaderInfoLog(shader);
                Console.WriteLine($"[GLControl] ERROR compilando {name}: {log}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// DPI scale of the screen the control is on (1.25 at Windows' 125% setting, 1.0 at 100%).
        /// <para>
        /// It must be read from the <see cref="Avalonia.Controls.TopLevel"/>, never from
        /// <c>VisualRoot</c>: in Avalonia 12 a control's visual root is a
        /// <c>Avalonia.Controls.TopLevelHost</c>, which does **not** derive from <c>TopLevel</c>,
        /// so the old <c>VisualRoot as TopLevel</c> cast silently returned null and the scale fell
        /// back to 1.0. Avalonia still sized the framebuffer as <c>Bounds * RenderScaling</c>, so at
        /// 125% the viewport covered only 1/1.25 = 80% of it and the picture stopped short of the
        /// window edges (bottom-left corner, the rest left at the clear colour).
        /// </para>
        /// </summary>
        private double ScreenScaling =>
            (_topLevel ??= Avalonia.Controls.TopLevel.GetTopLevel(this))?.RenderScaling ?? 1.0;

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// GLSL version to ask the desktop shaders for, derived from what the driver reports in
        /// <c>GL_SHADING_LANGUAGE_VERSION</c> ("1.40", "3.30 NVIDIA…", "4.60 …").
        /// <para>
        /// Only two answers matter: <b>150</b> (GL 3.2+) and <b>140</b> (GL 3.1). The shader body
        /// uses nothing newer than 1.40, but the choice cannot be fixed either way — the Raspberry
        /// Pi's V3D driver caps at GL 3.1/GLSL 1.40 and rejects <c>#version 150</c>, while macOS
        /// core profiles reject anything *below* 150.
        /// </para>
        /// </summary>
        private int DesktopGlslVersion(string glslVersionString)
        {
            // Leading "<major>.<minor>" of the version string, ignoring the vendor suffix.
            string head = (glslVersionString ?? "").TrimStart().Split(' ')[0];
            string[] parts = head.Split('.');

            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int major) &&
                int.TryParse(parts[1].Length >= 2 ? parts[1].Substring(0, 2) : parts[1] + "0", out int minor))
            {
                return major * 100 + minor >= 150 ? 150 : 140;
            }

            // Unparseable string: fall back to the context Avalonia created (GL 3.2 -> GLSL 1.50).
            var v = this.GlVersion;
            return (v.Major > 3 || (v.Major == 3 && v.Minor >= 2)) ? 150 : 140;
        }

        private void CacheUniformLocations(ShaderProgram p)
        {
            p.Texture    = _gl.GetUniformLocation(p.Id, "uTexture");
            p.SourceSize = _gl.GetUniformLocation(p.Id, "uSourceSize");
            p.OutputSize = _gl.GetUniformLocation(p.Id, "uOutputSize");
            p.Time       = _gl.GetUniformLocation(p.Id, "uTime");
            p.Curvature  = _gl.GetUniformLocation(p.Id, "uCurvature");
            p.Vignette   = _gl.GetUniformLocation(p.Id, "uVignette");
            p.Scanline   = _gl.GetUniformLocation(p.Id, "uScanline");
            p.ChromAb    = _gl.GetUniformLocation(p.Id, "uChromAb");
            p.Bloom      = _gl.GetUniformLocation(p.Id, "uBloom");
            p.Mask       = _gl.GetUniformLocation(p.Id, "uMask");
            p.Noise      = _gl.GetUniformLocation(p.Id, "uNoise");
            p.TexMin     = _gl.GetUniformLocation(p.Id, "uTexMin");
            p.TexMax     = _gl.GetUniformLocation(p.Id, "uTexMax");
            p.Texel      = _gl.GetUniformLocation(p.Id, "uTexel");
            p.Dir        = _gl.GetUniformLocation(p.Id, "uDir");
            p.AreaTex    = _gl.GetUniformLocation(p.Id, "uAreaTex");
            p.SearchTex  = _gl.GetUniformLocation(p.Id, "uSearchTex");
            p.BlendTex   = _gl.GetUniformLocation(p.Id, "uBlendTex");
            p.Subpix     = _gl.GetUniformLocation(p.Id, "uSubpix");
            p.Threshold  = _gl.GetUniformLocation(p.Id, "uThreshold");
            p.TexSize     = _gl.GetUniformLocation(p.Id, "uTexSize");
            p.Sharp       = _gl.GetUniformLocation(p.Id, "uSharp");
            p.MaskType    = _gl.GetUniformLocation(p.Id, "uMaskType");
            p.MaskScale   = _gl.GetUniformLocation(p.Id, "uMaskScale");
            p.ChromaRadius = _gl.GetUniformLocation(p.Id, "uChromaRadius");
            p.LumaSoftness = _gl.GetUniformLocation(p.Id, "uLumaSoftness");
            p.Artefacts    = _gl.GetUniformLocation(p.Id, "uArtefacts");
            p.Frame       = _gl.GetUniformLocation(p.Id, "uFrame");
            p.PrevTex     = _gl.GetUniformLocation(p.Id, "uPrevTex");
            p.Persistence = _gl.GetUniformLocation(p.Id, "uPersistence");
        }

        /// <summary>
        /// Compiles, links and caches the uniform locations of one program. A failure is reported
        /// and left as <c>Valid = false</c>: the render path falls back to the CRT program, and if
        /// that is the one that failed it simply draws nothing, as before.
        /// </summary>
        private ShaderProgram BuildProgram(string vertSrc, string fragSrc, string name)
        {
            var p = new ShaderProgram();

            uint vs = _gl.CreateShader(GLEnum.VertexShader);
            _gl.ShaderSource(vs, vertSrc);
            _gl.CompileShader(vs);
            bool vsOk = CheckShader(vs, $"Vertex Shader ({name})");

            uint fs = _gl.CreateShader(GLEnum.FragmentShader);
            _gl.ShaderSource(fs, fragSrc);
            _gl.CompileShader(fs);
            bool fsOk = CheckShader(fs, $"Fragment Shader ({name})");

            p.Id = _gl.CreateProgram();
            _gl.AttachShader(p.Id, vs);
            _gl.AttachShader(p.Id, fs);

            _gl.BindAttribLocation(p.Id, 0, "aPos");
            _gl.BindAttribLocation(p.Id, 1, "aTexCoord");

            _gl.LinkProgram(p.Id);
            _gl.GetProgram(p.Id, GLEnum.LinkStatus, out int linkStatus);
            if (linkStatus == 0)
            {
                ColoredConsole.WriteLine($"[GLControl] ERROR link ({name}): [[red]]{_gl.GetProgramInfoLog(p.Id)}[[/red]]", Config.ConfigOptions.DebugModes.Quiet);
                p.Valid = false;
            }
            else
            {
                p.Valid = vsOk && fsOk;
                ColoredConsole.WriteLine($"[GLControl] [[green]]GL linked ok ({name}).[[/green]]", Config.ConfigOptions.DebugModes.Quiet);
            }

            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            if (p.Valid)
            {
                CacheUniformLocations(p);

                // Constant uniforms, set once per program. The crop is identity by default and
                // updated each frame from ShowBorders.
                _gl.UseProgram(p.Id);
                if (p.Texture    >= 0) _gl.Uniform1(p.Texture, 0);
                if (p.SourceSize >= 0) _gl.Uniform2(p.SourceSize, (float)_srcW, (float)_srcH);
                if (p.TexMin     >= 0) _gl.Uniform2(p.TexMin, 0f, 0f);
                if (p.TexMax     >= 0) _gl.Uniform2(p.TexMax, 1f, 1f);

                // The SMAA passes read their second and third textures from fixed units.
                if (p.AreaTex    >= 0) _gl.Uniform1(p.AreaTex, 1);
                if (p.SearchTex  >= 0) _gl.Uniform1(p.SearchTex, 2);
                if (p.BlendTex   >= 0) _gl.Uniform1(p.BlendTex, 1);
                if (p.PrevTex    >= 0) _gl.Uniform1(p.PrevTex, 1);
            }

            return p;
        }

        /// <summary>
        /// Program to draw this frame: the plain blit when the user turned the CRT effects off —
        /// or always in monochrome high resolution, where a sharp pixel image is what's wanted and
        /// CRT effects make no sense — the full shader otherwise (and also if the plain one failed
        /// to build). The user's DisableCrtEffects preference is left untouched, so it comes back
        /// when a colour monitor is selected again.
        /// </summary>
        private ShaderProgram ActiveProgram =>
            (VideoTiming.Mono || Config.ConfigOptions.RunninConfig.DisableCrtEffects) && _plainProgram != null && _plainProgram.Valid
                ? _plainProgram
                : _crtProgram;

        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            _gl = GL.GetApi(gl.GetProcAddress);

            string glVersion = _gl.GetStringS(GLEnum.Version) ?? "Unknown";
            string glRenderer = _gl.GetStringS(GLEnum.Renderer) ?? "Unknown";
            string glslVer = _gl.GetStringS(GLEnum.ShadingLanguageVersion) ?? "Unknown";

            // Which shader set to use is decided by the context Avalonia actually created, not by
            // sniffing GL_VERSION for "OpenGL ES": that string is the driver's, and it says nothing
            // reliable about the profile (Mesa reports plain "3.1 Mesa …" for a desktop GLX context
            // on the same Raspberry Pi whose EGL path gives an ES one).
            bool isES = this.GlVersion.Type == GlProfileType.OpenGLES;
            int glslVersion = isES ? 100 : DesktopGlslVersion(glslVer);

            if (Config.ConfigOptions.RunninConfig.DebugMode >= Config.ConfigOptions.DebugModes.Quiet)
            {
                ColoredConsole.WriteLine($"[GLControl] GL Version  : [[green]]{glVersion}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GL Renderer : [[green]]{glRenderer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GLSL Version: [[green]]{glslVer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] Context     : [[green]]{(isES ? "OpenGL ES" : "Desktop OpenGL")} {this.GlVersion.Major}.{this.GlVersion.Minor}[[/green]], shaders: [[green]]#version {glslVersion}[[/green]]");
            }

            string vertSrc      = isES ? VertexShaderES  : $"#version {glslVersion}\n{VertexShaderDesktop}";
            string fragSrc      = isES ? CrtFragmentES   : $"#version {glslVersion}\n{CrtFragmentDesktop}";
            string fragPlainSrc = isES ? PlainFragmentES : $"#version {glslVersion}\n{PlainFragmentDesktop}";

            // Both programs are built up front — they are two small shaders, and building the
            // plain one lazily would stall the first frame after the user flips the switch.
            _crtProgram   = BuildProgram(vertSrc, fragSrc, "CRT");
            _plainProgram = BuildProgram(vertSrc, fragPlainSrc, "plain");

            // Built up front like the other two, even though most sessions never switch the
            // filter on: it is a small program, and compiling it on the first frame after the
            // user ticks the box would stall that frame.
            string fragDitherSrc = isES ? DitherFragmentES : $"#version {glslVersion}\n{DitherFragmentDesktop}";
            _ditherProgram = BuildProgram(vertSrc, fragDitherSrc, "dither");

            string fragGradSrc = isES ? GradientFragmentES : $"#version {glslVersion}\n{GradientFragmentDesktop}";
            _gradientProgram = BuildProgram(vertSrc, fragGradSrc, "gradient");

            string fragEagleSrc = isES ? EagleFragmentES : $"#version {glslVersion}\n{EagleFragmentDesktop}";
            _eagleProgram = BuildProgram(vertSrc, fragEagleSrc, "smooth borders");

            string fragXbrSrc = isES ? XbrFragmentES : $"#version {glslVersion}\n{XbrFragmentDesktop}";
            _xbrProgram = BuildProgram(vertSrc, fragXbrSrc, "xBR");

            string fragSignalSrc = isES ? SignalFragmentES : $"#version {glslVersion}\n{SignalFragmentDesktop}";
            _signalProgram = BuildProgram(vertSrc, fragSignalSrc, "video signal");

            string fragPersistSrc = isES ? PersistFragmentES : $"#version {glslVersion}\n{PersistFragmentDesktop}";
            _persistProgram = BuildProgram(vertSrc, fragPersistSrc, "persistence");

            // Anti-aliasing: four more small programs, built up front like the rest, and the
            // two lookup tables SMAA needs.
            string fragFxaaSrc = isES ? FxaaFragmentES : $"#version {glslVersion}\n{FxaaFragmentDesktop}";
            _fxaaProgram = BuildProgram(vertSrc, fragFxaaSrc, "FXAA");

            string fragSmaaEdgeSrc   = isES ? SmaaEdgeFragmentES   : $"#version {glslVersion}\n{SmaaEdgeFragmentDesktop}";
            string fragSmaaWeightSrc = isES ? SmaaWeightFragmentES : $"#version {glslVersion}\n{SmaaWeightFragmentDesktop}";
            string fragSmaaBlendSrc  = isES ? SmaaBlendFragmentES  : $"#version {glslVersion}\n{SmaaBlendFragmentDesktop}";
            _smaaEdgeProgram   = BuildProgram(vertSrc, fragSmaaEdgeSrc,   "SMAA edges");
            _smaaWeightProgram = BuildProgram(vertSrc, fragSmaaWeightSrc, "SMAA weights");
            _smaaBlendProgram  = BuildProgram(vertSrc, fragSmaaBlendSrc,  "SMAA blend");
            LoadSmaaLookups();

            float[] vertices =
            {
                -1.0f,  1.0f,  0.0f, 0.0f,
                -1.0f, -1.0f,  0.0f, 1.0f,
                 1.0f, -1.0f,  1.0f, 1.0f,
                 1.0f,  1.0f,  1.0f, 0.0f
            };

            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

            fixed (void* v = vertices)
                _gl.BufferData(GLEnum.ArrayBuffer, (uint)(vertices.Length * sizeof(float)), v, GLEnum.StaticDraw);

            // VAO
            _hasVao = false;
            try
            {
                _vao = _gl.GenVertexArray();
                if (_vao != 0)
                {
                    _hasVao = true;
                    _gl.BindVertexArray(_vao);
                    _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

                    _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
                    _gl.EnableVertexAttribArray(0);

                    _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                    _gl.EnableVertexAttribArray(1);

                    _gl.BindVertexArray(0);
                    ColoredConsole.WriteLine($"[GLControl] VAO ok id=[[yellow]]{_vao}[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                }
                else
                {
                    ColoredConsole.WriteLine("[GLControl] GenVertexArray = [[yellow]]0[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                }
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"[GLControl] VAO not available -> [[red]]{ex.Message}[[/red]]", Config.ConfigOptions.DebugModes.Quiet);
                _hasVao = false;
                _vao = 0;
            }

            _gl.BindBuffer(GLEnum.ArrayBuffer, 0);

            _textureId = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, _textureId);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

            // Allocated black at the maximum geometry rather than left undefined: the first
            // frames are drawn before the emulation has published anything, and nothing is
            // uploaded until it does. EnsureTextureGeometry resizes it to the active mode on the
            // first render (and again whenever the monitor type changes).
            uint[] blank = new uint[VideoTiming.MAX_WIDTH * VideoTiming.MAX_HEIGHT];
            fixed (void* pBlank = blank)
                _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, VideoTiming.MAX_WIDTH, VideoTiming.MAX_HEIGHT, 0,
                               GLEnum.Rgba, GLEnum.UnsignedByte, pBlank);

            _timer.Restart();
        }

        /// <summary>
        /// Resizes the texture to the active video geometry (colour 832x288 / mono 640x400) when
        /// VideoTiming's geometry generation changes — i.e. after a reset that switched the monitor
        /// type. Also refreshes uSourceSize on both programs. Cheap no-op on the common frame.
        /// </summary>
        private unsafe void EnsureTextureGeometry()
        {
            int gen = VideoTiming.GeometryGeneration;
            if (gen == _geomGen) return;

            _geomGen = gen;
            _srcW = VideoTiming.BUFFER_WIDTH;
            _srcH = VideoTiming.BUFFER_HEIGHT;

            _gl.BindTexture(GLEnum.Texture2D, _textureId);
            uint[] blank = new uint[_srcW * _srcH];
            fixed (void* pBlank = blank)
                _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)_srcW, (uint)_srcH, 0,
                               GLEnum.Rgba, GLEnum.UnsignedByte, pBlank);

            foreach (var p in new[] { _crtProgram, _plainProgram })
            {
                if (p != null && p.Valid && p.SourceSize >= 0)
                {
                    _gl.UseProgram(p.Id);
                    _gl.Uniform2(p.SourceSize, (float)_srcW, (float)_srcH);
                }
            }

            // Force the next AcquireFrame to hand over a buffer so the freshly-sized texture is
            // filled (its bookmark would otherwise still match and skip the upload).
            _frameSeq = -1;
            _signalDirty = true;
            _ditherDirty = true;
            _eagleDirty = true;
            _aaDirty = true;
            _persistDirty = true;
        }

        /// <summary>
        /// Creates (or resizes) one target of the filter chain: a texture <paramref name="scale"/>
        /// times the size of the emulator's framebuffer and an FBO pointing at it. Returns false
        /// when the driver refuses the FBO.
        /// </summary>
        private unsafe bool CreateFilterTarget(ref uint tex, ref uint fbo, bool linear, string name, int scale = 1)
        {
            uint w = (uint)(_srcW * scale);
            uint h = (uint)(_srcH * scale);

            if (tex == 0) tex = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, tex);
            _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, w, h, 0,
                           GLEnum.Rgba, GLEnum.UnsignedByte, (void*)0);

            // The last texture of the chain is what the CRT/plain pass scales up to the window,
            // so it wants the filtering the raw one had; the intermediate one is only ever read
            // texel by texel and stays NEAREST for good.
            int filter = linear ? (int)GLEnum.Linear : (int)GLEnum.Nearest;
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, filter);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, filter);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

            if (fbo == 0) fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(GLEnum.Framebuffer, fbo);
            _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0,
                                     GLEnum.Texture2D, tex, 0);

            GLEnum status = (GLEnum)_gl.CheckFramebufferStatus(GLEnum.Framebuffer);
            bool ok = status == GLEnum.FramebufferComplete;

            if (ok)
                ColoredConsole.WriteLine($"[GLControl] [[green]]{name} target ok[[/green]] {w}x{h}", Config.ConfigOptions.DebugModes.Quiet);
            else
                ColoredConsole.WriteLine($"[GLControl] ERROR {name} FBO: [[red]]{status}[[/red]]");

            return ok;
        }

        /// <summary>
        /// Creates (or resizes) the targets of the colorization chain. Returns false when it
        /// cannot run at all — the first program failed to build, or the driver refuses the FBO —
        /// in which case the picture is simply drawn unfiltered. A failure is remembered, not
        /// retried every frame. The gradient half degrades on its own: without its program or its
        /// target the chain stops after the dithering pass, which is still a picture worth having.
        /// <para>Restores Avalonia's framebuffer before returning: it binds its own to attach the
        /// textures, and the caller is in the middle of drawing a frame into <paramref name="fb"/>.
        /// </para>
        /// </summary>
        private unsafe bool EnsureDitherTarget(uint fb)
        {
            if (_ditherProgram == null || !_ditherProgram.Valid) return false;
            if (_ditherGeom == _geomGen) return _ditherFboValid;

            _ditherGeom = _geomGen;

            _ditherFboValid = CreateFilterTarget(ref _ditherTexture, ref _ditherFbo, true, "dither");
            _gradientFboValid = _ditherFboValid
                                && _gradientProgram != null && _gradientProgram.Valid
                                && CreateFilterTarget(ref _gradientTexture, ref _gradientFbo, false, "gradient");

            if (!_ditherFboValid)
                ColoredConsole.WriteLine("[GLControl] [[red]]colorize dithering disabled[[/red]]");
            else if (!_gradientFboValid)
                ColoredConsole.WriteLine("[GLControl] [[yellow]]gradient reconstruction unavailable[[/yellow]] — dithering colorized without it");

            _ditherDirty = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
            return _ditherFboValid;
        }

        /// <summary>
        /// Runs the whole colorization chain over the emulator framebuffer: the dithering pass
        /// from the uploaded texture into <c>_ditherTexture</c>, then — when it is available — the
        /// gradient pass vertically into <c>_gradientTexture</c> and back horizontally into
        /// <c>_ditherTexture</c>, which is what the final pass samples either way. Called at most
        /// once per published frame; the vertex state is the caller's (the same full-screen quad
        /// every pass draws).
        /// </summary>
        private void RunColorizeChain(uint fb)
        {
            _gl.Viewport(0, 0, (uint)_srcW, (uint)_srcH);

            // ---- dithering pass: raw texture -> _ditherTexture.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _ditherFbo);
            _gl.UseProgram(_ditherProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            // The filter compares texels for equality, so it must read them, not the blend of a
            // tap that landed a fraction off the centre. Restored below — the final pass scales
            // the picture to the window and wants LINEAR back.
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);

            if (_ditherProgram.Texture >= 0) _gl.Uniform1(_ditherProgram.Texture, 0);
            if (_ditherProgram.Texel   >= 0) _gl.Uniform2(_ditherProgram.Texel, 1f / _srcW, 1f / _srcH);

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);

            // ---- gradient pass, separable: vertical first, then horizontal back into the
            // texture the first pass wrote. Each draw reads one target and writes the other, so
            // neither ever samples what it is rendering into.
            if (_gradientFboValid)
            {
                _gl.UseProgram(_gradientProgram.Id);
                if (_gradientProgram.Texture >= 0) _gl.Uniform1(_gradientProgram.Texture, 0);
                if (_gradientProgram.Texel   >= 0) _gl.Uniform2(_gradientProgram.Texel, 1f / _srcW, 1f / _srcH);

                _gl.BindFramebuffer(GLEnum.Framebuffer, _gradientFbo);
                _gl.BindTexture(GLEnum.Texture2D, _ditherTexture);
                _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
                _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
                if (_gradientProgram.Dir >= 0) _gl.Uniform2(_gradientProgram.Dir, 0f, 1f);
                _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

                _gl.BindFramebuffer(GLEnum.Framebuffer, _ditherFbo);
                _gl.BindTexture(GLEnum.Texture2D, _gradientTexture);
                if (_gradientProgram.Dir >= 0) _gl.Uniform2(_gradientProgram.Dir, 1f, 0f);
                _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

                _gl.BindTexture(GLEnum.Texture2D, _ditherTexture);
                _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
                _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            }

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        /// <summary>
        /// Creates (or resizes) the video-signal target: one texture the size of the framebuffer,
        /// LINEAR like every texture the final pass may end up sampling. Returns false when the
        /// pass cannot run (program failed to build, or the driver refuses the FBO), in which
        /// case the picture is drawn as an RGB monitor would show it. A failure is remembered,
        /// not retried every frame. Restores Avalonia's framebuffer before returning.
        /// </summary>
        private unsafe bool EnsureSignalTarget(uint fb)
        {
            if (_signalProgram == null || !_signalProgram.Valid) return false;
            if (_signalGeom == _geomGen) return _signalFboValid;

            _signalGeom = _geomGen;
            _signalFboValid = CreateFilterTarget(ref _signalTexture, ref _signalFbo, true, "video signal");

            if (!_signalFboValid)
                ColoredConsole.WriteLine("[GLControl] [[red]]video signal emulation disabled[[/red]]");

            _signalDirty = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
            return _signalFboValid;
        }

        /// <summary>
        /// Runs the composite/RF pass over the raw frame into <c>_signalTexture</c>. Once per
        /// published frame; the frame counter it is handed is what moves the dot crawl.
        /// </summary>
        private void RunSignalPass(uint fb, Config.ConfigOptions.VideoSignals signal)
        {
            _gl.Viewport(0, 0, (uint)_srcW, (uint)_srcH);
            _gl.BindFramebuffer(GLEnum.Framebuffer, _signalFbo);
            _gl.UseProgram(_signalProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            if (_signalProgram.Texture    >= 0) _gl.Uniform1(_signalProgram.Texture, 0);
            if (_signalProgram.Texel      >= 0) _gl.Uniform2(_signalProgram.Texel, 1f / _srcW, 1f / _srcH);
            if (_signalProgram.SourceSize >= 0) _gl.Uniform2(_signalProgram.SourceSize, (float)_srcW, (float)_srcH);

            // The three knobs of each signal, from the config file (see Config.RfArtefacts),
            // clamped here so a typo cannot break the picture.
            var cfg = Config.ConfigOptions.RunninConfig;
            bool rf = signal == Config.ConfigOptions.VideoSignals.RF;
            float radius    = Math.Clamp(rf ? cfg.RfChromaRadius : cfg.CompositeChromaRadius, 2f, 8f);
            float softness  = Math.Clamp(rf ? cfg.RfLumaSoftness : cfg.CompositeLumaSoftness, 0f, 1f);
            float artefacts = Math.Clamp(rf ? cfg.RfArtefacts    : cfg.CompositeArtefacts,    0f, 1f);
            if (_signalProgram.ChromaRadius >= 0) _gl.Uniform1(_signalProgram.ChromaRadius, radius);
            if (_signalProgram.LumaSoftness >= 0) _gl.Uniform1(_signalProgram.LumaSoftness, softness);
            if (_signalProgram.Artefacts    >= 0) _gl.Uniform1(_signalProgram.Artefacts, artefacts);
            if (_signalProgram.Frame      >= 0) _gl.Uniform1(_signalProgram.Frame, (float)(_frameCounter & 7));

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        /// <summary>
        /// Creates (or resizes) the edge-smoothing target — shared by SuperEagle and xBR, both
        /// 2x — one texture at twice the framebuffer's size, LINEAR because it is what the final
        /// pass scales up to the window. Returns false when the requested filter cannot run (its
        /// program failed to build, or the driver refuses the FBO), in which case the picture is
        /// simply drawn without it. A failure is remembered, not retried every frame.
        /// <para>Restores Avalonia's framebuffer before returning, like
        /// <see cref="EnsureDitherTarget"/>: the caller is in the middle of drawing a frame into
        /// <paramref name="fb"/>.</para>
        /// </summary>
        private unsafe bool EnsureEagleTarget(uint fb, Config.ConfigOptions.EdgeSmoothings mode)
        {
            ShaderProgram prog = mode == Config.ConfigOptions.EdgeSmoothings.XBR ? _xbrProgram : _eagleProgram;
            if (prog == null || !prog.Valid) return false;
            if (_eagleGeom == _geomGen) return _eagleFboValid;

            _eagleGeom = _geomGen;
            _eagleFboValid = CreateFilterTarget(ref _eagleTexture, ref _eagleFbo, true, "edge smoothing", 2);

            if (!_eagleFboValid)
                ColoredConsole.WriteLine("[GLControl] [[red]]edge smoothing disabled[[/red]]");

            _eagleDirty = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
            return _eagleFboValid;
        }

        /// <summary>
        /// Runs the edge-smoothing pass (SuperEagle or xBR, by <paramref name="mode"/>) over
        /// <paramref name="source"/> — the raw frame, or the last texture of the colorization
        /// chain when that is on — into <c>_eagleTexture</c> at twice the size. Called at most
        /// once per published frame; the vertex state is the caller's.
        /// </summary>
        private void RunEaglePass(uint fb, uint source, Config.ConfigOptions.EdgeSmoothings mode)
        {
            ShaderProgram prog = mode == Config.ConfigOptions.EdgeSmoothings.XBR ? _xbrProgram : _eagleProgram;

            _gl.Viewport(0, 0, (uint)(_srcW * 2), (uint)(_srcH * 2));
            _gl.BindFramebuffer(GLEnum.Framebuffer, _eagleFbo);
            _gl.UseProgram(prog.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, source);

            // Same reason as the dithering pass: both filters compare texels for equality, so
            // they must read them and not a blend. Every possible source is LINEAR otherwise
            // (they are what gets scaled to the window when this pass is off), so it is put back.
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);

            if (prog.Texture    >= 0) _gl.Uniform1(prog.Texture, 0);
            if (prog.Texel      >= 0) _gl.Uniform2(prog.Texel, 1f / _srcW, 1f / _srcH);
            if (prog.SourceSize >= 0) _gl.Uniform2(prog.SourceSize, (float)_srcW, (float)_srcH);

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        /// <summary>
        /// Creates (or resizes) the two persistence targets — the output and the history copy —
        /// for an input <paramref name="scale"/> times the framebuffer's size, keyed on the
        /// scale as well as the geometry like the anti-aliasing ones. A rebuild leaves the
        /// history holding nothing, so the next run copies instead of blending. Returns false
        /// when the pass cannot run. Restores Avalonia's framebuffer before returning.
        /// </summary>
        private unsafe bool EnsurePersistTarget(uint fb, int scale)
        {
            if (_persistProgram == null || !_persistProgram.Valid) return false;
            if (_persistGeom == _geomGen && _persistScale == scale) return _persistFboValid;

            _persistGeom  = _geomGen;
            _persistScale = scale;

            _persistFboValid = CreateFilterTarget(ref _persistTexture, ref _persistFbo, true, "persistence", scale)
                               && CreateFilterTarget(ref _persistPrevTexture, ref _persistPrevFbo, true, "persistence history", scale);

            if (!_persistFboValid)
                ColoredConsole.WriteLine("[GLControl] [[red]]phosphor persistence disabled[[/red]]");

            _persistHistoryValid = false;
            _persistDirty = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
            return _persistFboValid;
        }

        /// <summary>
        /// Blends <paramref name="source"/> — the chain's output for this frame — with the
        /// previous frame into <c>_persistTexture</c>, <paramref name="share"/> being the
        /// previous frame's weight, then copies the current frame into the history texture for
        /// the next one. Two draws of the same program (the copy is the blend with the share at
        /// 0). The first run after a rebuild has no history and copies only.
        /// </summary>
        private void RunPersistPass(uint fb, uint source, int scale, float share)
        {
            int w = _srcW * scale, h = _srcH * scale;

            _gl.Viewport(0, 0, (uint)w, (uint)h);
            _gl.UseProgram(_persistProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture1);
            _gl.BindTexture(GLEnum.Texture2D, _persistPrevTexture);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, source);

            if (_persistProgram.Texture >= 0) _gl.Uniform1(_persistProgram.Texture, 0);
            if (_persistProgram.PrevTex >= 0) _gl.Uniform1(_persistProgram.PrevTex, 1);

            // 1. The blend, into the output.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _persistFbo);
            if (_persistProgram.Persistence >= 0)
                _gl.Uniform1(_persistProgram.Persistence, _persistHistoryValid ? Math.Clamp(share, 0f, 0.5f) : 0f);
            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            // 2. The copy of this frame, into the history.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _persistPrevFbo);
            if (_persistProgram.Persistence >= 0) _gl.Uniform1(_persistProgram.Persistence, 0f);
            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _persistHistoryValid = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        /// <summary>
        /// Uploads SMAA's two lookup tables from the embedded resources: the area texture
        /// (160x560, two bytes per texel: the coverage on each side of an edge, indexed by the
        /// distances to its ends and the crossing edges there) and the search texture (64x16,
        /// one byte: how many texels the last bilinear fetch of a search covered). Both are the
        /// reference implementation's, byte for byte. Without them the SMAA mode is reported
        /// unavailable and <see cref="EnsureAaTarget"/> refuses it; FXAA does not need them.
        /// <para>They are expanded to RGBA on the way in: RG and R formats are not in GL ES 2.0
        /// and LUMINANCE_ALPHA is gone from the desktop core profile, so four bytes per texel is
        /// the one layout every context takes. The area table is LINEAR — the weight pass
        /// interpolates between its entries, the distances being square-rooted — and the search
        /// table NEAREST, read at texel centres.</para>
        /// </summary>
        private unsafe void LoadSmaaLookups()
        {
            const int AreaW = 160, AreaH = 560, SearchW = 64, SearchH = 16;

            byte[] area   = ReadEmbeddedResource("ASE.Resources.SMAA_AreaTex.bin");
            byte[] search = ReadEmbeddedResource("ASE.Resources.SMAA_SearchTex.bin");

            if (area == null || area.Length != AreaW * AreaH * 2 ||
                search == null || search.Length != SearchW * SearchH)
            {
                _smaaLookupsValid = false;
                ColoredConsole.WriteLine("[GLControl] [[red]]SMAA lookup tables missing or damaged[[/red]] — the SMAA anti-aliasing mode is unavailable");
                return;
            }

            byte[] areaRgba = new byte[AreaW * AreaH * 4];
            for (int i = 0; i < AreaW * AreaH; i++)
            {
                areaRgba[i * 4]     = area[i * 2];
                areaRgba[i * 4 + 1] = area[i * 2 + 1];
                areaRgba[i * 4 + 2] = 0;
                areaRgba[i * 4 + 3] = 255;
            }

            byte[] searchRgba = new byte[SearchW * SearchH * 4];
            for (int i = 0; i < SearchW * SearchH; i++)
            {
                searchRgba[i * 4]     = search[i];
                searchRgba[i * 4 + 1] = search[i];
                searchRgba[i * 4 + 2] = search[i];
                searchRgba[i * 4 + 3] = 255;
            }

            _gl.ActiveTexture(GLEnum.Texture0);
            _smaaAreaTexture   = UploadLookupTexture(areaRgba, AreaW, AreaH, true);
            _smaaSearchTexture = UploadLookupTexture(searchRgba, SearchW, SearchH, false);
            _smaaLookupsValid  = true;

            ColoredConsole.WriteLine("[GLControl] [[green]]SMAA lookup tables ok[[/green]]", Config.ConfigOptions.DebugModes.Quiet);
        }

        private unsafe uint UploadLookupTexture(byte[] rgba, int w, int h, bool linear)
        {
            uint tex = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, tex);

            int filter = linear ? (int)GLEnum.Linear : (int)GLEnum.Nearest;
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, filter);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, filter);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

            fixed (void* p = rgba)
                _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)w, (uint)h, 0,
                               GLEnum.Rgba, GLEnum.UnsignedByte, p);

            return tex;
        }

        private static byte[] ReadEmbeddedResource(string name)
        {
            try
            {
                using System.IO.Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) return null;

                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"[GLControl] could not read the embedded resource {name}: [[red]]{ex.Message}[[/red]]");
                return null;
            }
        }

        /// <summary>
        /// Creates (or resizes) the anti-aliasing targets for an input <paramref name="scale"/>
        /// times the framebuffer's size: the output texture (LINEAR, it is what the final pass
        /// scales to the window) and, for SMAA, its two intermediates — the edges texture,
        /// LINEAR because the weight pass fetches two edge texels at a time through the filter,
        /// and the weights texture, NEAREST. Keyed on the geometry <i>and</i> the scale, since
        /// the input is 2x while the SuperEagle pass is on and 1x otherwise. Returns false when
        /// the requested mode cannot run — its programs failed to build, SMAA's lookup tables
        /// are missing, or the driver refuses an FBO — and the picture is then drawn without it.
        /// A failure is remembered, not retried every frame.
        /// <para>Restores Avalonia's framebuffer before returning, like the other Ensure*
        /// methods: the caller is in the middle of drawing a frame into <paramref name="fb"/>.
        /// </para>
        /// </summary>
        private unsafe bool EnsureAaTarget(uint fb, int scale, bool smaa)
        {
            bool smaaAvailable = _smaaLookupsValid
                                 && _smaaEdgeProgram   != null && _smaaEdgeProgram.Valid
                                 && _smaaWeightProgram != null && _smaaWeightProgram.Valid
                                 && _smaaBlendProgram  != null && _smaaBlendProgram.Valid;
            bool fxaaAvailable = _fxaaProgram != null && _fxaaProgram.Valid;

            if (smaa ? !smaaAvailable : !fxaaAvailable) return false;

            if (_aaGeom == _geomGen && _aaScale == scale)
                return smaa ? _aaSmaaFboValid : _aaFboValid;

            _aaGeom  = _geomGen;
            _aaScale = scale;

            _aaFboValid = CreateFilterTarget(ref _aaTexture, ref _aaFbo, true, "anti-aliasing", scale);

            // SMAA's intermediates are built alongside whether or not SMAA is the mode selected
            // right now: they are two small textures, and building them lazily would stall the
            // frame the user switches to it.
            _aaSmaaFboValid = _aaFboValid && smaaAvailable
                              && CreateFilterTarget(ref _aaEdgesTexture,   ref _aaEdgesFbo,   true,  "SMAA edges",   scale)
                              && CreateFilterTarget(ref _aaWeightsTexture, ref _aaWeightsFbo, false, "SMAA weights", scale);

            if (!_aaFboValid)
                ColoredConsole.WriteLine("[GLControl] [[red]]anti-aliasing disabled[[/red]]");
            else if (smaaAvailable && !_aaSmaaFboValid)
                ColoredConsole.WriteLine("[GLControl] [[yellow]]SMAA unavailable[[/yellow]] — FXAA still works");

            _aaDirty = true;

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
            return smaa ? _aaSmaaFboValid : _aaFboValid;
        }

        /// <summary>
        /// Runs FXAA over <paramref name="source"/> — the raw frame, the colorized one or the
        /// SuperEagle 2x, whichever the chain ended on — into <c>_aaTexture</c> at that same
        /// size. Called at most once per published frame; the vertex state is the caller's.
        /// FXAA reads its end-of-edge probes through the bilinear filter, and every possible
        /// source is LINEAR by the time it gets here (the passes before it put it back), so
        /// there is no filtering to set.
        /// </summary>
        private void RunFxaaPass(uint fb, uint source, int scale)
        {
            int w = _srcW * scale, h = _srcH * scale;

            _gl.Viewport(0, 0, (uint)w, (uint)h);
            _gl.BindFramebuffer(GLEnum.Framebuffer, _aaFbo);
            _gl.UseProgram(_fxaaProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, source);

            if (_fxaaProgram.Texture >= 0) _gl.Uniform1(_fxaaProgram.Texture, 0);
            if (_fxaaProgram.Texel   >= 0) _gl.Uniform2(_fxaaProgram.Texel, 1f / w, 1f / h);
            if (_fxaaProgram.Subpix  >= 0) _gl.Uniform1(_fxaaProgram.Subpix, Math.Clamp(Config.ConfigOptions.RunninConfig.FxaaSubpix, 0f, 1f));

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        /// <summary>
        /// Runs the three SMAA passes over <paramref name="source"/> into <c>_aaTexture</c>:
        /// edge detection into the edges texture, blending weights from it (plus the two lookup
        /// tables on units 1 and 2) into the weights texture, and the neighbourhood blending of
        /// the source by those weights. Same contract as <see cref="RunFxaaPass"/>. Units 1 and
        /// 2 are left bound; the final pass only ever uses unit 0 and rebinds it itself.
        /// </summary>
        private void RunSmaaPasses(uint fb, uint source, int scale)
        {
            int w = _srcW * scale, h = _srcH * scale;
            float tx = 1f / w, ty = 1f / h;

            _gl.Viewport(0, 0, (uint)w, (uint)h);

            // ---- 1. edges: source -> _aaEdgesTexture.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _aaEdgesFbo);
            _gl.UseProgram(_smaaEdgeProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, source);
            if (_smaaEdgeProgram.Texture   >= 0) _gl.Uniform1(_smaaEdgeProgram.Texture, 0);
            if (_smaaEdgeProgram.Texel     >= 0) _gl.Uniform2(_smaaEdgeProgram.Texel, tx, ty);
            if (_smaaEdgeProgram.Threshold >= 0) _gl.Uniform1(_smaaEdgeProgram.Threshold, Math.Clamp(Config.ConfigOptions.RunninConfig.SmaaThreshold, 0.01f, 0.5f));
            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            // ---- 2. blending weights: edges (0) + area (1) + search (2) -> _aaWeightsTexture.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _aaWeightsFbo);
            _gl.UseProgram(_smaaWeightProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture2);
            _gl.BindTexture(GLEnum.Texture2D, _smaaSearchTexture);
            _gl.ActiveTexture(GLEnum.Texture1);
            _gl.BindTexture(GLEnum.Texture2D, _smaaAreaTexture);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _aaEdgesTexture);
            if (_smaaWeightProgram.Texture    >= 0) _gl.Uniform1(_smaaWeightProgram.Texture, 0);
            if (_smaaWeightProgram.AreaTex    >= 0) _gl.Uniform1(_smaaWeightProgram.AreaTex, 1);
            if (_smaaWeightProgram.SearchTex  >= 0) _gl.Uniform1(_smaaWeightProgram.SearchTex, 2);
            if (_smaaWeightProgram.Texel      >= 0) _gl.Uniform2(_smaaWeightProgram.Texel, tx, ty);
            if (_smaaWeightProgram.SourceSize >= 0) _gl.Uniform2(_smaaWeightProgram.SourceSize, (float)w, (float)h);
            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            // ---- 3. neighbourhood blending: source (0) + weights (1) -> _aaTexture.
            _gl.BindFramebuffer(GLEnum.Framebuffer, _aaFbo);
            _gl.UseProgram(_smaaBlendProgram.Id);
            _gl.ActiveTexture(GLEnum.Texture1);
            _gl.BindTexture(GLEnum.Texture2D, _aaWeightsTexture);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, source);
            if (_smaaBlendProgram.Texture  >= 0) _gl.Uniform1(_smaaBlendProgram.Texture, 0);
            if (_smaaBlendProgram.BlendTex >= 0) _gl.Uniform1(_smaaBlendProgram.BlendTex, 1);
            if (_smaaBlendProgram.Texel    >= 0) _gl.Uniform2(_smaaBlendProgram.Texel, tx, ty);
            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            _gl.BindFramebuffer(GLEnum.Framebuffer, fb);
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            ShaderProgram prog = ActiveProgram;
            if (_gl == null || prog == null || prog.Id == 0 || !prog.Valid) return;

            long profileRenderStart = FrameProfiler.Stamp();

            _gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);

            EnsureTextureGeometry();

            // DPI-aware surface size: the same pixel size Avalonia gives the framebuffer
            // (Bounds * RenderScaling, truncated).
            double scaling = ScreenScaling;
            uint surfaceW = (uint)Math.Max(1, (int)(Bounds.Width  * scaling));
            uint surfaceH = (uint)Math.Max(1, (int)(Bounds.Height * scaling));

            // Letterbox: the picture keeps DisplayAspectRatio whatever shape the control is,
            // centred, with the leftover cleared to black. In a normal window the window itself
            // is kept at the right ratio (MainWindow.EnforceAspectRatio) so this is usually a
            // no-op, but it is what makes the two cases where it cannot be work: full screen
            // (the window is the screen and its shape is not ours to choose) and the transient
            // shapes of an interactive resize — which used to stretch the picture until the
            // 300 ms debounce corrected the window.
            double ratio = DisplayAspectRatio;
            uint outW = surfaceW, outH = surfaceH;

            if (!Config.ConfigOptions.RunninConfig.StretchToFill)
            {
                if (surfaceW > surfaceH * ratio)
                    outW = (uint)Math.Max(1, Math.Round(surfaceH * ratio));   // too wide -> pillarbox
                else
                    outH = (uint)Math.Max(1, Math.Round(surfaceW / ratio));   // too tall -> letterbox

                // Integer scaling: the fit shrinks to a whole number of pixels per ST line. The
                // width follows the aspect ratio and so stays fractional — the sharp sampling of
                // the final pass keeps it crisp — and when not even one pixel per line fits, the
                // plain fit stands.
                if (Config.ConfigOptions.RunninConfig.Scaling == Config.ConfigOptions.ScalingModes.Integer)
                {
                    double lines = VideoTiming.Mono || Config.ConfigOptions.RunninConfig.ShowBorders
                        ? VideoTiming.BUFFER_HEIGHT
                        : VideoTiming.DISPLAY_TEX_HEIGHT;
                    int perLine = (int)Math.Floor(outH / lines);
                    if (perLine >= 1)
                    {
                        outH = (uint)(perLine * lines);
                        outW = (uint)Math.Max(1, Math.Round(outH * ratio));
                    }
                }
            }

            int outX = (int)(surfaceW - outW) / 2;
            int outY = (int)(surfaceH - outH) / 2;

            // Log on first frame and whenever the DPI scale changes (window moved to a
            // monitor with a different scale) — this is what users should report when the
            // picture doesn't fill the window.
            if (_firstRender || scaling != _lastLoggedScaling)
            {
                ColoredConsole.WriteLine($"[GLControl] fb=[[yellow]]{fb}[[/yellow]], hasVao=[[yellow]]{_hasVao}[[/yellow]], scale=[[yellow]]{scaling:0.##}[[/yellow]], viewport=[[yellow]]{outW}x{outH}[[/yellow]]+[[yellow]]{outX},{outY}[[/yellow]], surface=[[yellow]]{surfaceW}x{surfaceH}[[/yellow]], bounds=[[yellow]]{Bounds.Width:0.#}x{Bounds.Height:0.#}[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                _firstRender = false;
                _lastLoggedScaling = scaling;
            }

            // Reset
            _gl.Disable(GLEnum.CullFace);
            _gl.Disable(GLEnum.DepthTest);
            _gl.Disable(GLEnum.StencilTest);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(false);

            // Vertex state. Set before the colorization chain because every pass draws the
            // same full-screen quad with it.
            if (_hasVao)
            {
                _gl.BindVertexArray(_vao);
            }
            else
            {
                _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
                _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
                _gl.EnableVertexAttribArray(0);
                _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                _gl.EnableVertexAttribArray(1);
            }

            // Upload the emulation's latest frame, if there is a new one. AcquireFrame hands over
            // the buffer itself (no copy) and returns null when the texture is already up to date,
            // in which case the picture is simply redrawn from what the texture already holds.
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            long profileUpload = FrameProfiler.Stamp();
            uint[] frame = ASEMain.AcquireFrame(ref _frameSeq);
            if (frame != null)
            {
                fixed (void* pData = frame)
                {
                    _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)_srcW, (uint)_srcH,
                                      GLEnum.Rgba, GLEnum.UnsignedByte, pData);
                }
            }
            long profileUploadEnd = FrameProfiler.Stamp();

            // ---- The filter chain. Every stage below is a pass of its own into an offscreen
            // texture, and the final pass samples whatever texture the chain ended on. A stage
            // runs only when its input differs from the last time it ran: `changed` starts as
            // "a new frame was uploaded" and turns true the moment any stage runs or is
            // switched, so everything downstream follows and nothing upstream repeats. The GL
            // thread redraws free-running, several times per emulated frame, and this rule is
            // what makes a chain this heavy affordable at all.
            //
            // The order is the order of the signal path, and every step of it is load-bearing:
            // the video signal first, because it is what the television received; then the
            // corrections that detect pixel patterns by comparing texels for equality
            // (colorization, edge smoothing) — bypassed when a composite signal has already
            // blurred the picture, since it leaves them nothing to find; then anti-aliasing,
            // which blends colours along edges and would defeat those detectors if it ran
            // first; then the phosphor persistence, a blend of consecutive frames that has to
            // see the finished frame. The final pass adds the screen itself.
            var cfg = Config.ConfigOptions.RunninConfig;
            uint sourceTexture = _textureId;
            int  sourceScale   = 1;
            bool changed = frame != null;

            if (frame != null) _frameCounter++;

            // ---- 1. Video signal: composite / RF, at the source resolution.
            var signal = cfg.VideoSignal;
            bool rgb = signal == Config.ConfigOptions.VideoSignals.RGB;

            if (signal != _signalMode)
            {
                _signalMode = signal;
                _signalDirty = true;
                changed = true;
            }

            if (!rgb && EnsureSignalTarget((uint)fb))
            {
                if (changed || _signalDirty)
                {
                    RunSignalPass((uint)fb, signal);
                    _signalDirty = false;
                    changed = true;
                }
                sourceTexture = _signalTexture;
            }

            // ---- 2. Dithering colorization: passes of their own into offscreen textures the
            // size of the framebuffer, the last of which the next stage samples in place of the
            // raw one. Pattern-based, so only on an RGB picture.
            bool colorize = rgb && cfg.ColorizeDithering;

            if (colorize != _ditherEnabled)
            {
                _ditherEnabled = colorize;
                _ditherDirty = true;
                changed = true;
            }

            if (colorize && EnsureDitherTarget((uint)fb))
            {
                if (changed || _ditherDirty)
                {
                    RunColorizeChain((uint)fb);
                    _ditherDirty = false;
                    changed = true;
                }
                sourceTexture = _ditherTexture;
            }

            // ---- 3. Edge smoothing (SuperEagle or xBR) into a target twice the framebuffer's
            // size; pattern-based like the stage above, so RGB only. At 2x it covers four times
            // the texels of everything before it.
            var smoothing = rgb ? cfg.EdgeSmoothing : Config.ConfigOptions.EdgeSmoothings.None;

            if (smoothing != _eagleMode)
            {
                _eagleMode = smoothing;
                _eagleDirty = true;
                changed = true;
            }

            if (smoothing != Config.ConfigOptions.EdgeSmoothings.None && EnsureEagleTarget((uint)fb, smoothing))
            {
                if (changed || _eagleDirty)
                {
                    RunEaglePass((uint)fb, sourceTexture, smoothing);
                    _eagleDirty = false;
                    changed = true;
                }
                sourceTexture = _eagleTexture;
                sourceScale   = 2;
            }

            // ---- 4. Anti-aliasing: FXAA or SMAA over whatever the chain produced so far, at
            // that texture's own size.
            var aaMode = cfg.AntiAliasing;

            if (aaMode != _aaMode)
            {
                _aaMode = aaMode;
                _aaDirty = true;
                changed = true;
            }

            if (aaMode != Config.ConfigOptions.AntiAliasingModes.None &&
                EnsureAaTarget((uint)fb, sourceScale, aaMode == Config.ConfigOptions.AntiAliasingModes.SMAA))
            {
                if (changed || _aaDirty)
                {
                    if (aaMode == Config.ConfigOptions.AntiAliasingModes.SMAA)
                        RunSmaaPasses((uint)fb, sourceTexture, sourceScale);
                    else
                        RunFxaaPass((uint)fb, sourceTexture, sourceScale);
                    _aaDirty = false;
                    changed = true;
                }
                sourceTexture = _aaTexture;
            }

            // ---- 5. Phosphor persistence: this frame blended with the previous one. Re-running
            // it on a switch without a new frame is harmless — the history is a copy of the
            // previous frame, not of the previous output, so it never compounds.
            int persistence = Math.Clamp(cfg.Persistence, 0, 50);
            bool persist = persistence > 0;

            if (persist != _persistEnabled)
            {
                _persistEnabled = persist;
                _persistDirty = true;
                changed = true;
            }

            if (persist && EnsurePersistTarget((uint)fb, sourceScale))
            {
                if (changed || _persistDirty)
                {
                    RunPersistPass((uint)fb, sourceTexture, sourceScale, persistence / 100f);
                    _persistDirty = false;
                    changed = true;
                }
                sourceTexture = _persistTexture;
            }

            // Final pass: the CRT or plain program, into Avalonia's framebuffer.
            _gl.Viewport(outX, outY, outW, outH);

            // Black, and it matters: with the scissor test off (disabled just above) this clear
            // covers the whole surface, so it is what paints the letterbox bars around the
            // viewport set above. A grey there would frame the picture on every screen wider
            // than 4:3.
            _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            _gl.Clear((uint)GLEnum.ColorBufferBit);

            _gl.UseProgram(prog.Id);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, sourceTexture);

            // Uniforms per frame. The effect ones are simply absent (-1) from the plain program,
            // so this same block sends nothing extra when the effects are switched off.
            float time = (float)_timer.Elapsed.TotalSeconds;

            if (prog.OutputSize >= 0) _gl.Uniform2(prog.OutputSize, (float)outW, (float)outH);
            if (prog.Time       >= 0) _gl.Uniform1(prog.Time, time);

            // The texture being sampled may be the raw frame or a 2x one; the scanline pitch
            // (uSourceSize) stays the raw line count either way.
            if (prog.TexSize   >= 0) _gl.Uniform2(prog.TexSize, (float)(_srcW * sourceScale), (float)(_srcH * sourceScale));
            if (prog.Sharp     >= 0) _gl.Uniform1(prog.Sharp, cfg.Scaling == Config.ConfigOptions.ScalingModes.Smooth ? 0f : 1f);
            if (prog.MaskType  >= 0) _gl.Uniform1(prog.MaskType, (float)(int)cfg.MaskType);
            if (prog.MaskScale >= 0) _gl.Uniform1(prog.MaskScale, (float)Math.Max(1, Math.Round(scaling)));

            if (prog.Curvature >= 0) _gl.Uniform1(prog.Curvature, Config.ConfigOptions.RunninConfig.Curvature);
            if (prog.Vignette  >= 0) _gl.Uniform1(prog.Vignette,  Config.ConfigOptions.RunninConfig.Vignette);
            if (prog.Scanline  >= 0) _gl.Uniform1(prog.Scanline,  Config.ConfigOptions.RunninConfig.Scanline);
            if (prog.ChromAb   >= 0) _gl.Uniform1(prog.ChromAb,   Config.ConfigOptions.RunninConfig.ChromAb);
            if (prog.Bloom     >= 0) _gl.Uniform1(prog.Bloom,     Config.ConfigOptions.RunninConfig.Bloom);
            if (prog.Mask      >= 0) _gl.Uniform1(prog.Mask,      Config.ConfigOptions.RunninConfig.Mask);
            if (prog.Noise     >= 0) _gl.Uniform1(prog.Noise,     Config.ConfigOptions.RunninConfig.Noise);

            // Border crop: identity (full texture) when borders are shown or in monochrome (which
            // has none), otherwise the 320x200 display sub-rectangle so the picture fills the
            // window as before.
            if (prog.TexMin >= 0 || prog.TexMax >= 0)
            {
                bool crop = !VideoTiming.Mono && !Config.ConfigOptions.RunninConfig.ShowBorders;
                float xMin = crop ? (float)VideoTiming.DISPLAY_ORIGIN_X / _srcW : 0f;
                float yMin = crop ? (float)VideoTiming.DISPLAY_ORIGIN_Y / _srcH : 0f;
                float xMax = crop ? (float)(VideoTiming.DISPLAY_ORIGIN_X + VideoTiming.DISPLAY_TEX_WIDTH) / _srcW : 1f;
                float yMax = crop ? (float)(VideoTiming.DISPLAY_ORIGIN_Y + VideoTiming.DISPLAY_TEX_HEIGHT) / _srcH : 1f;
                if (prog.TexMin >= 0) _gl.Uniform2(prog.TexMin, xMin, yMin);
                if (prog.TexMax >= 0) _gl.Uniform2(prog.TexMax, xMax, yMax);
            }

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            // Report to --profile. Only the CPU side of drawing is timed here: the draw call
            // returns without waiting for the GPU, so a shader too heavy for the hardware shows
            // up as a lower GL frame rate rather than as a longer render time.
            if (FrameProfiler.Enabled)
                FrameProfiler.ReportGlFrame(Stopwatch.GetTimestamp() - profileRenderStart,
                                            profileUploadEnd - profileUpload,
                                            frame != null);

            RequestNextFrameRendering();
        }



        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _gl?.DeleteBuffer(_vbo);
            if (_hasVao && _vao != 0)
                _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteTexture(_textureId);
            if (_ditherFbo != 0) _gl?.DeleteFramebuffer(_ditherFbo);
            if (_ditherTexture != 0) _gl?.DeleteTexture(_ditherTexture);
            if (_gradientFbo != 0) _gl?.DeleteFramebuffer(_gradientFbo);
            if (_gradientTexture != 0) _gl?.DeleteTexture(_gradientTexture);
            if (_eagleFbo != 0) _gl?.DeleteFramebuffer(_eagleFbo);
            if (_eagleTexture != 0) _gl?.DeleteTexture(_eagleTexture);
            if (_aaFbo != 0) _gl?.DeleteFramebuffer(_aaFbo);
            if (_aaTexture != 0) _gl?.DeleteTexture(_aaTexture);
            if (_aaEdgesFbo != 0) _gl?.DeleteFramebuffer(_aaEdgesFbo);
            if (_aaEdgesTexture != 0) _gl?.DeleteTexture(_aaEdgesTexture);
            if (_aaWeightsFbo != 0) _gl?.DeleteFramebuffer(_aaWeightsFbo);
            if (_aaWeightsTexture != 0) _gl?.DeleteTexture(_aaWeightsTexture);
            if (_smaaAreaTexture != 0) _gl?.DeleteTexture(_smaaAreaTexture);
            if (_smaaSearchTexture != 0) _gl?.DeleteTexture(_smaaSearchTexture);
            if (_signalFbo != 0) _gl?.DeleteFramebuffer(_signalFbo);
            if (_signalTexture != 0) _gl?.DeleteTexture(_signalTexture);
            if (_persistFbo != 0) _gl?.DeleteFramebuffer(_persistFbo);
            if (_persistTexture != 0) _gl?.DeleteTexture(_persistTexture);
            if (_persistPrevFbo != 0) _gl?.DeleteFramebuffer(_persistPrevFbo);
            if (_persistPrevTexture != 0) _gl?.DeleteTexture(_persistPrevTexture);
            if (_crtProgram != null && _crtProgram.Id != 0) _gl?.DeleteProgram(_crtProgram.Id);
            if (_plainProgram != null && _plainProgram.Id != 0) _gl?.DeleteProgram(_plainProgram.Id);
            if (_ditherProgram != null && _ditherProgram.Id != 0) _gl?.DeleteProgram(_ditherProgram.Id);
            if (_gradientProgram != null && _gradientProgram.Id != 0) _gl?.DeleteProgram(_gradientProgram.Id);
            if (_eagleProgram != null && _eagleProgram.Id != 0) _gl?.DeleteProgram(_eagleProgram.Id);
            if (_fxaaProgram != null && _fxaaProgram.Id != 0) _gl?.DeleteProgram(_fxaaProgram.Id);
            if (_smaaEdgeProgram != null && _smaaEdgeProgram.Id != 0) _gl?.DeleteProgram(_smaaEdgeProgram.Id);
            if (_smaaWeightProgram != null && _smaaWeightProgram.Id != 0) _gl?.DeleteProgram(_smaaWeightProgram.Id);
            if (_smaaBlendProgram != null && _smaaBlendProgram.Id != 0) _gl?.DeleteProgram(_smaaBlendProgram.Id);
            if (_xbrProgram != null && _xbrProgram.Id != 0) _gl?.DeleteProgram(_xbrProgram.Id);
            if (_signalProgram != null && _signalProgram.Id != 0) _gl?.DeleteProgram(_signalProgram.Id);
            if (_persistProgram != null && _persistProgram.Id != 0) _gl?.DeleteProgram(_persistProgram.Id);
            _ditherFbo = 0;
            _ditherTexture = 0;
            _ditherFboValid = false;
            _gradientFbo = 0;
            _gradientTexture = 0;
            _gradientFboValid = false;
            _ditherGeom = -1;
            _eagleFbo = 0;
            _eagleTexture = 0;
            _eagleFboValid = false;
            _eagleGeom = -1;
            _aaFbo = 0;
            _aaTexture = 0;
            _aaEdgesFbo = 0;
            _aaEdgesTexture = 0;
            _aaWeightsFbo = 0;
            _aaWeightsTexture = 0;
            _aaFboValid = false;
            _aaSmaaFboValid = false;
            _aaGeom = -1;
            _aaScale = 0;
            _smaaAreaTexture = 0;
            _smaaSearchTexture = 0;
            _smaaLookupsValid = false;
            _signalFbo = 0;
            _signalTexture = 0;
            _signalFboValid = false;
            _signalGeom = -1;
            _persistFbo = 0;
            _persistTexture = 0;
            _persistPrevFbo = 0;
            _persistPrevTexture = 0;
            _persistFboValid = false;
            _persistHistoryValid = false;
            _persistGeom = -1;
            _persistScale = 0;
            _crtProgram = null;
            _plainProgram = null;
            _ditherProgram = null;
            _gradientProgram = null;
            _eagleProgram = null;
            _fxaaProgram = null;
            _smaaEdgeProgram = null;
            _smaaWeightProgram = null;
            _smaaBlendProgram = null;
            _xbrProgram = null;
            _signalProgram = null;
            _persistProgram = null;
            base.OnOpenGlDeinit(gl);
        }
    }
}
