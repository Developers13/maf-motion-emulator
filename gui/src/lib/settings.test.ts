import { describe, expect, it } from "vitest";
import {
  DEFAULT_SETTINGS,
  AppSettings,
  normalizeSettings,
  serializeSettings,
} from "./settings";

describe("normalizeSettings", () => {
  it("returns full defaults for an empty object", () => {
    expect(normalizeSettings({})).toEqual(DEFAULT_SETTINGS);
  });

  it("keeps valid non-blank strings and falls back the rest", () => {
    const s = normalizeSettings({
      PythonLauncher: "python3",
      MuMuManagerPath: "C:/x.exe",
      TemplateVmName: "虚拟机",
      LastStudentId: "S001",
    });
    expect(s.PythonLauncher).toBe("python3");
    expect(s.MuMuManagerPath).toBe("C:/x.exe");
    expect(s.TemplateVmName).toBe("虚拟机");
    expect(s.LastStudentId).toBe("S001");
    expect(s.ProjectRoot).toBe(DEFAULT_SETTINGS.ProjectRoot);
    expect(s.ScriptHostPath).toBe(DEFAULT_SETTINGS.ScriptHostPath);
    expect(s.WeekLimit).toBe(DEFAULT_SETTINGS.WeekLimit);
  });

  it("treats blank strings as missing for non-path fields", () => {
    const s = normalizeSettings({
      PythonLauncher: "   ",
      MuMuManagerPath: "\t",
      TemplateVmName: "",
      LastStudentId: "  ",
    });
    expect(s.PythonLauncher).toBe(DEFAULT_SETTINGS.PythonLauncher);
    expect(s.MuMuManagerPath).toBe(DEFAULT_SETTINGS.MuMuManagerPath);
    expect(s.TemplateVmName).toBe(DEFAULT_SETTINGS.TemplateVmName);
    expect(s.LastStudentId).toBe(DEFAULT_SETTINGS.LastStudentId);
  });

  it("keeps ProjectRoot / ScriptHostPath blanks, and maps null to empty", () => {
    const a = normalizeSettings({ ProjectRoot: "   ", ScriptHostPath: "x" });
    expect(a.ProjectRoot).toBe("   "); // 空白原样保留
    expect(a.ScriptHostPath).toBe("x");
    const b = normalizeSettings({ ProjectRoot: null, ScriptHostPath: null });
    expect(b.ProjectRoot).toBe("");
    expect(b.ScriptHostPath).toBe("");
  });

  it("Environment: null / non-object → default dict; values coerced to string; {} kept", () => {
    expect(normalizeSettings({ Environment: null }).Environment).toEqual({
      PYTHONUTF8: "1",
    });
    expect(normalizeSettings({ Environment: "nope" }).Environment).toEqual({
      PYTHONUTF8: "1",
    });
    expect(normalizeSettings({ Environment: [1, 2] }).Environment).toEqual({
      PYTHONUTF8: "1",
    });
    const coerced = normalizeSettings({ Environment: { PORT: 5123, ON: true } });
    expect(coerced.Environment).toEqual({ PORT: "5123", ON: "true" });
    expect(normalizeSettings({ Environment: {} }).Environment).toEqual({});
  });

  it("SelectedStudents: null / non-array → []; coerces elements; drops empties", () => {
    expect(normalizeSettings({}).SelectedStudents).toEqual([]);
    expect(normalizeSettings({ SelectedStudents: null }).SelectedStudents).toEqual([]);
    expect(normalizeSettings({ SelectedStudents: "x" }).SelectedStudents).toEqual([]);
    expect(
      normalizeSettings({ SelectedStudents: [1, 2.5, "a"] }).SelectedStudents,
    ).toEqual(["1", "2.5", "a"]);
    expect(
      normalizeSettings({ SelectedStudents: ["s1", "", "  ", "s2"] }).SelectedStudents,
    ).toEqual(["s1", "  ", "s2"]);
  });

  it("WeekLimit / TotalLimit: <=0 or non-number → default", () => {
    const s = normalizeSettings({
      WeekLimit: 0,
      TotalLimit: -1,
      WeekLimit2: "ignore",
    });
    expect(s.WeekLimit).toBe(DEFAULT_SETTINGS.WeekLimit);
    expect(s.TotalLimit).toBe(DEFAULT_SETTINGS.TotalLimit);
    expect(normalizeSettings({ WeekLimit: "5" }).WeekLimit).toBe(DEFAULT_SETTINGS.WeekLimit);
    expect(normalizeSettings({ WeekLimit: 3 }).WeekLimit).toBe(3);
    expect(normalizeSettings({ TotalLimit: 10 }).TotalLimit).toBe(10);
  });

  it("AutoScroll: non-boolean → true; real booleans kept", () => {
    expect(normalizeSettings({ AutoScroll: "yes" }).AutoScroll).toBe(true);
    expect(normalizeSettings({ AutoScroll: 0 }).AutoScroll).toBe(true);
    expect(normalizeSettings({ AutoScroll: "false" }).AutoScroll).toBe(true);
    expect(normalizeSettings({ AutoScroll: false }).AutoScroll).toBe(false);
    expect(normalizeSettings({ AutoScroll: true }).AutoScroll).toBe(true);
  });

  it("ignores unknown fields", () => {
    const s = normalizeSettings({ Foo: "bar", Bar: 1, PythonLauncher: "py" } as unknown);
    expect(s).not.toHaveProperty("Foo");
    expect(s).not.toHaveProperty("Bar");
    expect(s.PythonLauncher).toBe("py");
  });
});

describe("serializeSettings", () => {
  it("emits 2-space JSON with the fixed PascalCase field order and no BOM", () => {
    const s: AppSettings = {
      PythonLauncher: "python3",
      ProjectRoot: "root",
      Environment: { A: "1", B: "2" },
      MuMuManagerPath: "mumu",
      TemplateVmName: "vm",
      ScriptHostPath: "host",
      LastStudentId: "123",
      SelectedStudents: ["a", "b"],
      WeekLimit: 7,
      TotalLimit: 40,
      AutoScroll: false,
    };
    const expected = [
      "{",
      '  "PythonLauncher": "python3",',
      '  "ProjectRoot": "root",',
      '  "Environment": {',
      '    "A": "1",',
      '    "B": "2"',
      "  },",
      '  "MuMuManagerPath": "mumu",',
      '  "TemplateVmName": "vm",',
      '  "ScriptHostPath": "host",',
      '  "LastStudentId": "123",',
      '  "SelectedStudents": [',
      '    "a",',
      '    "b"',
      "  ],",
      '  "WeekLimit": 7,',
      '  "TotalLimit": 40,',
      '  "AutoScroll": false',
      "}",
    ].join("\n");
    const out = serializeSettings(s);
    expect(out).toBe(expected);
    // 无 BOM
    expect(out.charCodeAt(0)).not.toBe(0xfeff);
    // 字段按接口顺序出现
    const order = [
      "PythonLauncher",
      "ProjectRoot",
      "Environment",
      "MuMuManagerPath",
      "TemplateVmName",
      "ScriptHostPath",
      "LastStudentId",
      "SelectedStudents",
      "WeekLimit",
      "TotalLimit",
      "AutoScroll",
    ];
    for (let i = 0; i < order.length - 1; i += 1) {
      const a = out.indexOf(`"${order[i]}"`);
      const b = out.indexOf(`"${order[i + 1]}"`);
      expect(a).toBeGreaterThanOrEqual(0);
      expect(a).toBeLessThan(b);
    }
  });

  it("round-trips with normalizeSettings", () => {
    const s = normalizeSettings({
      PythonLauncher: "python3",
      SelectedStudents: [1, "a"],
      WeekLimit: 2,
      TotalLimit: 8,
      Environment: { X: 1 },
      AutoScroll: false,
    });
    const json = serializeSettings(s);
    const back = normalizeSettings(JSON.parse(json) as unknown);
    expect(back).toEqual(s);
  });
});
