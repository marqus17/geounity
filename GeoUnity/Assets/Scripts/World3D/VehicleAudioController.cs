using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Synthesizes seamless looping procedural audio for ULM (propeller engine + wind)
    /// and Car (forward engine + reverse gear whine + braking tire screech) with zero external audio assets.
    /// </summary>
    public class VehicleAudioController : MonoBehaviour
    {
        private const int SampleRate = 44100;
        private const float LoopSeconds = 2.0f;

        private AudioSource _ulmEngineSrc;
        private AudioSource _ulmWindSrc;
        private AudioSource _carEngineSrc;
        private AudioSource _carReverseSrc;
        private AudioSource _carScreechSrc;

        private bool _initialized;

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            _ulmEngineSrc  = CreateLoopSource("Audio_UlmEngine",  GenerateUlmPropClip());
            _ulmWindSrc    = CreateLoopSource("Audio_UlmWind",    GenerateWindClip());
            _carEngineSrc  = CreateLoopSource("Audio_CarEngine",  GenerateCarEngineClip());
            _carReverseSrc = CreateLoopSource("Audio_CarReverse", GenerateCarReverseClip());
            _carScreechSrc = CreateLoopSource("Audio_CarScreech", GenerateTireScreechClip());
        }

        public void UpdateUlmAudio(float speed, float minSpeed, float maxSpeed, bool active)
        {
            EnsureInitialized();
            SetSourceActive(_carEngineSrc, false);
            SetSourceActive(_carReverseSrc, false);
            SetSourceActive(_carScreechSrc, false);

            if (!active)
            {
                SetSourceActive(_ulmEngineSrc, false);
                SetSourceActive(_ulmWindSrc, false);
                return;
            }

            float norm = Mathf.InverseLerp(minSpeed, maxSpeed, speed);
            SetSourceActive(_ulmEngineSrc, true);
            _ulmEngineSrc.pitch = Mathf.Lerp(0.75f, 1.65f, norm);
            _ulmEngineSrc.volume = Mathf.Lerp(0.18f, 0.34f, norm);

            SetSourceActive(_ulmWindSrc, true);
            _ulmWindSrc.pitch = Mathf.Lerp(0.85f, 1.35f, norm);
            _ulmWindSrc.volume = Mathf.Lerp(0.05f, 0.16f, norm);
        }

        public void UpdateCarAudio(float speed, float maxSpeed, bool braking, bool active)
        {
            EnsureInitialized();
            SetSourceActive(_ulmEngineSrc, false);
            SetSourceActive(_ulmWindSrc, false);

            if (!active)
            {
                SetSourceActive(_carEngineSrc, false);
                SetSourceActive(_carReverseSrc, false);
                SetSourceActive(_carScreechSrc, false);
                return;
            }

            float absSpeed = Mathf.Abs(speed);
            float norm = Mathf.Clamp01(absSpeed / Mathf.Max(1f, maxSpeed));

            // Forward / idle engine rumble
            SetSourceActive(_carEngineSrc, true);
            _carEngineSrc.pitch = Mathf.Lerp(0.68f, 1.85f, norm);
            _carEngineSrc.volume = Mathf.Lerp(0.10f, 0.30f, norm);

            // Typical straight-cut gear whine when reversing
            bool reversing = speed < -0.5f;
            SetSourceActive(_carReverseSrc, reversing);
            if (reversing)
            {
                float revNorm = Mathf.Clamp01(absSpeed / 14f);
                _carReverseSrc.pitch = Mathf.Lerp(0.80f, 1.45f, revNorm);
                _carReverseSrc.volume = Mathf.Lerp(0.08f, 0.24f, revNorm);
            }

            // Tire screech when braking while moving forward at speed
            bool screeching = braking && speed > 4.5f;
            SetSourceActive(_carScreechSrc, screeching);
            if (screeching)
            {
                float brkNorm = Mathf.Clamp01((speed - 4.5f) / 22f);
                _carScreechSrc.pitch = Mathf.Lerp(0.92f, 1.12f, brkNorm);
                _carScreechSrc.volume = Mathf.Lerp(0.08f, 0.22f, brkNorm);
            }
        }

        public void MuteAll()
        {
            if (!_initialized) return;
            SetSourceActive(_ulmEngineSrc, false);
            SetSourceActive(_ulmWindSrc, false);
            SetSourceActive(_carEngineSrc, false);
            SetSourceActive(_carReverseSrc, false);
            SetSourceActive(_carScreechSrc, false);
        }

        private AudioSource CreateLoopSource(string goName, AudioClip clip)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 0f;
            return src;
        }

        private static void SetSourceActive(AudioSource src, bool play)
        {
            if (src == null) return;
            if (play && !src.isPlaying) src.Play();
            else if (!play && src.isPlaying) src.Stop();
        }

        // --- Procedural seamless AudioClip generators (exact integer cycles per LoopSeconds -> zero click at seam) ---
        private static AudioClip GenerateUlmPropClip()
        {
            int n = Mathf.RoundToInt(SampleRate * LoopSeconds);
            float[] data = new float[n];
            // 90 Hz fundamental (exact 180 cycles in 2s) + harmonics + 30 Hz propeller blade chop
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float buzz = Mathf.Sin(2f * Mathf.PI * 90f * t) * 0.45f
                           + Mathf.Sin(2f * Mathf.PI * 180f * t) * 0.28f
                           + Mathf.Sin(2f * Mathf.PI * 270f * t) * 0.16f
                           + Mathf.Sin(2f * Mathf.PI * 360f * t) * 0.08f;
                float propChop = 0.72f + 0.28f * Mathf.Sin(2f * Mathf.PI * 30f * t);
                data[i] = Mathf.Clamp(buzz * propChop, -0.95f, 0.95f);
            }
            return MakeClip("UlmPropLoop", data);
        }

        private static AudioClip GenerateWindClip()
        {
            int n = Mathf.RoundToInt(SampleRate * LoopSeconds);
            float[] data = new float[n];
            var rng = new System.Random(101);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (white - lp) * 0.045f; // Smooth low-pass airy rush
                data[i] = Mathf.Clamp(lp * 2.6f, -0.95f, 0.95f);
            }
            CrossfadeSeam(data, 2048);
            return MakeClip("WindLoop", data);
        }

        private static AudioClip GenerateCarEngineClip()
        {
            int n = Mathf.RoundToInt(SampleRate * LoopSeconds);
            float[] data = new float[n];
            var rng = new System.Random(202);
            float lpNoise = 0f;
            // Warm 4-cylinder 62 Hz fundamental + rich lower harmonics
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float w = (float)(rng.NextDouble() * 2.0 - 1.0);
                lpNoise += (w - lpNoise) * 0.08f;
                float eng = Mathf.Sin(2f * Mathf.PI * 62f * t) * 0.48f
                          + Mathf.Sin(2f * Mathf.PI * 124f * t) * 0.30f
                          + Mathf.Sin(2f * Mathf.PI * 186f * t) * 0.15f
                          + Mathf.Sin(2f * Mathf.PI * 248f * t) * 0.07f
                          + lpNoise * 0.14f;
                data[i] = Mathf.Clamp(eng, -0.95f, 0.95f);
            }
            CrossfadeSeam(data, 1024);
            return MakeClip("CarEngineLoop", data);
        }

        private static AudioClip GenerateCarReverseClip()
        {
            int n = Mathf.RoundToInt(SampleRate * LoopSeconds);
            float[] data = new float[n];
            // Classic straight-cut reverse gear whine (510 Hz + 1020 Hz)
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float whine = Mathf.Sin(2f * Mathf.PI * 510f * t) * 0.55f
                            + Mathf.Sin(2f * Mathf.PI * 1020f * t) * 0.25f
                            + Mathf.Sin(2f * Mathf.PI * 765f * t) * 0.10f;
                data[i] = Mathf.Clamp(whine, -0.95f, 0.95f);
            }
            return MakeClip("CarReverseLoop", data);
        }

        private static AudioClip GenerateTireScreechClip()
        {
            int n = Mathf.RoundToInt(SampleRate * LoopSeconds);
            float[] data = new float[n];
            var rng = new System.Random(303);
            float grit = 0f;
            // Dual high-pitched rubber resonance (980 Hz + 1460 Hz) + subtle pitch wobble & friction grit
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float w = (float)(rng.NextDouble() * 2.0 - 1.0);
                grit += (w - grit) * 0.25f;
                float wob = Mathf.Sin(2f * Mathf.PI * 9f * t) * 18f;
                float s = Mathf.Sin(2f * Mathf.PI * (980f + wob) * t) * 0.42f
                        + Mathf.Sin(2f * Mathf.PI * (1460f - wob * 0.7f) * t) * 0.28f
                        + grit * 0.22f;
                data[i] = Mathf.Clamp(s, -0.95f, 0.95f);
            }
            CrossfadeSeam(data, 1024);
            return MakeClip("TireScreechLoop", data);
        }

        private static void CrossfadeSeam(float[] data, int fadeSamples)
        {
            int n = data.Length;
            int len = Mathf.Min(fadeSamples, n / 4);
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)len;
                data[i] = Mathf.Lerp(data[n - len + i], data[i], t);
            }
        }

        private static AudioClip MakeClip(string name, float[] samples)
        {
            var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
