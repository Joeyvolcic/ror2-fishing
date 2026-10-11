// Request #7 (approved): Old Boot shows "+2% fishing luck" everywhere - a placebo, the odds don't change.
using System; using System.Collections; using System.IO; using System.Reflection;
public static class BootPlacebo1 {
  const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
  public static void Run() {
    string log = Path.Combine(Path.GetTempPath(), "ror2_stuckfix.log"), done = "";
    foreach (var a in FishingScope.Assemblies()) {
      Type[] ts; try { ts = FishingScope.TypesOf(a); } catch (ReflectionTypeLoadException e) { ts = Array.FindAll(e.Types, t => t != null); } catch { continue; }
      foreach (Type t in ts) {
        if (t.Name.StartsWith("Fishing")) {          // perk text used in chat / HUD
          FieldInfo f = t.GetField("PerkText", S);
          if (f != null) { var d = f.GetValue(null) as IDictionary; if (d != null && d.Contains("Old Boot")) { d["Old Boot"] = "+2% fishing luck"; done += t.Name + " "; } }
        }
        if (t.Name.StartsWith("FishItems")) {        // item-bar description
          FieldInfo f = t.GetField("Perks", S);
          if (f != null) { var d = f.GetValue(null) as IDictionary; if (d != null && d.Contains("Old Boot")) { object p = d["Old Boot"]; p.GetType().GetField("text").SetValue(p, "+2% fishing luck"); done += t.Name + " "; } }
        }
      }
    }
    File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss ") + "old boot placebo text set on: " + done + Environment.NewLine);
  }
}
