using System; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using System.Reflection; using RoR2; using UnityEngine;
public static class FrameProf2 {
  const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
  static readonly string[] Events = { "onUpdate", "onFixedUpdate", "onLateUpdate" };
  static readonly Dictionary<string, Delegate[]> orig = new Dictionary<string, Delegate[]>();
  static readonly Dictionary<string, Action> wrap = new Dictionary<string, Action>();
  static readonly Dictionary<string, double> ms = new Dictionary<string, double>();
  static readonly Stopwatch sw = new Stopwatch();
  static float until, dtSum, dtMax; static int frames;
  static void Log(string s){ System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ror2_prof.log"), DateTime.Now.ToString("HH:mm:ss ")+s+Environment.NewLine); }
  public static void Run(){ RoR2Application.onUpdate += Start; }
  static void Start(){
    RoR2Application.onUpdate -= Start;
    orig.Clear(); wrap.Clear(); ms.Clear(); frames = 0; dtSum = 0; dtMax = 0;   // BepInEx: fresh numbers per run
    foreach (string ev in Events){
      FieldInfo f = typeof(RoR2Application).GetField(ev, All); if (f == null) { Log("no field " + ev); continue; }
      var cur = f.GetValue(null) as Action; if (cur == null) continue;
      Delegate[] list = cur.GetInvocationList(); orig[ev] = list; string e2 = ev;
      Action w = () => { foreach (Delegate d in list) { sw.Reset(); sw.Start(); try { ((Action)d)(); } catch (Exception x) { UnityEngine.Debug.LogException(x); } sw.Stop(); string k = e2 + " " + (d.Method.DeclaringType != null ? d.Method.DeclaringType.Name : "?") + "." + d.Method.Name; double v; ms.TryGetValue(k, out v); ms[k] = v + sw.Elapsed.TotalMilliseconds; } };
      wrap[ev] = w; f.SetValue(null, w);
    }
    until = Time.unscaledTime + 8f; RoR2Application.onLateUpdate += Tick; Log("profiling 8 s");
  }
  static void Tick(){
    frames++; dtSum += Time.unscaledDeltaTime; dtMax = Math.Max(dtMax, Time.unscaledDeltaTime);
    if (Time.unscaledTime < until) return;
    RoR2Application.onLateUpdate -= Tick;
    foreach (var kv in orig){
      FieldInfo f = typeof(RoR2Application).GetField(kv.Key, All);
      var now = f.GetValue(null) as Action; Delegate rebuilt = Delegate.Combine(kv.Value);
      if (now != null) foreach (Delegate d in now.GetInvocationList()) if (d != (Delegate)wrap[kv.Key] && !(d.Method.Name == "Tick" && d.Method.DeclaringType == typeof(FrameProf2))) rebuilt = Delegate.Combine(rebuilt, d);
      f.SetValue(null, rebuilt);
    }
    Log(string.Format("frames {0}, avg {1:0.0} ms ({2:0} fps), worst {3:0.0} ms", frames, 1000*dtSum/frames, frames/dtSum, 1000*dtMax));
    foreach (var kv in ms.OrderByDescending(k => k.Value).Take(25)) Log(string.Format("  {0,8:0.000} ms/frame  {1}", kv.Value/frames, kv.Key));
    Log("restored");
  }
}

