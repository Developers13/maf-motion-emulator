import { describe, expect, it } from "vitest";
import {
  weekKeyFor,
  weekIsCurrent,
  setFromRead,
  incrementAfterSuccess,
  parseStatusMap,
  serializeStatusMap,
  ExerciseStatusMap,
} from "./status";

describe("weekKeyFor", () => {
  it("returns the Monday of the local week for Mon/Fri/Sun", () => {
    // 周一 → 自身；周五 → 本周一；周日 → 上一周周一
    expect(weekKeyFor(new Date(2026, 10, 9))).toBe("2026-11-09"); // Mon 11/09
    expect(weekKeyFor(new Date(2026, 10, 13))).toBe("2026-11-09"); // Fri 11/13
    expect(weekKeyFor(new Date(2026, 10, 10))).toBe("2026-11-09"); // Tue 11/10
    expect(weekKeyFor(new Date(2026, 10, 8))).toBe("2026-11-02"); // Sun 11/08 → 上周一
  });

  it("handles year boundaries", () => {
    // 2026-01-01 属上一周（周一 2025-12-29）；12/28 周日 → 12/22
    expect(weekKeyFor(new Date(2026, 0, 1))).toBe("2025-12-29");
    expect(weekKeyFor(new Date(2025, 11, 28))).toBe("2025-12-22");
    expect(weekKeyFor(new Date(2025, 11, 29))).toBe("2025-12-29"); // 周一自身
  });
});

describe("weekIsCurrent", () => {
  it("true when WeekKey is the week of now, false otherwise", () => {
    const now = new Date(2026, 10, 13, 9, 0, 0);
    expect(weekIsCurrent({ Week: 0, Total: 0, WeekKey: "2026-11-09", ReadAt: "" }, now)).toBe(true);
    expect(weekIsCurrent({ Week: 0, Total: 0, WeekKey: "2026-11-02", ReadAt: "" }, now)).toBe(false);
    // 不传 now 用系统当前时间
    const todayKey = weekKeyFor(new Date());
    expect(weekIsCurrent({ Week: 0, Total: 0, WeekKey: todayKey, ReadAt: "" })).toBe(true);
    expect(weekIsCurrent({ Week: 0, Total: 0, WeekKey: "0000-00-00", ReadAt: "" })).toBe(false);
  });
});

describe("setFromRead", () => {
  it("clamps negatives and stamps current week + ISO ReadAt", () => {
    const now = new Date(2026, 10, 13, 12, 0, 0);
    const s = setFromRead(-1, -2, now);
    expect(s).toEqual({
      Week: 0,
      Total: 0,
      WeekKey: "2026-11-09",
      ReadAt: now.toISOString(),
    });
    const s2 = setFromRead(2, 5, now);
    expect(s2.Week).toBe(2);
    expect(s2.Total).toBe(5);
  });
});

describe("incrementAfterSuccess", () => {
  it("creates {Week:1,Total:1} when the student is absent", () => {
    const now = new Date(2026, 10, 13, 9, 0, 0);
    const after = incrementAfterSuccess({}, "S1", now);
    expect(after.S1).toEqual({
      Week: 1,
      Total: 1,
      WeekKey: "2026-11-09",
      ReadAt: now.toISOString(),
    });
  });

  it("increments within the same week", () => {
    const now = new Date(2026, 10, 13, 9, 0, 0);
    const before: ExerciseStatusMap = {
      S1: { Week: 2, Total: 10, WeekKey: "2026-11-09", ReadAt: "old" },
    };
    const after = incrementAfterSuccess(before, "S1", now);
    expect(after.S1).toEqual({
      Week: 3,
      Total: 11,
      WeekKey: "2026-11-09",
      ReadAt: now.toISOString(),
    });
  });

  it("resets Week to 0 (then +1) and keeps Total across a week boundary", () => {
    const now = new Date(2026, 10, 13, 9, 0, 0);
    const before: ExerciseStatusMap = {
      S1: { Week: 4, Total: 10, WeekKey: "2026-11-02", ReadAt: "old" },
    };
    const after = incrementAfterSuccess(before, "S1", now);
    expect(after.S1).toEqual({
      Week: 1,
      Total: 11,
      WeekKey: "2026-11-09",
      ReadAt: now.toISOString(),
    });
  });

  it("does not mutate the input map or status object", () => {
    const now = new Date(2026, 10, 13, 9, 0, 0);
    const before: ExerciseStatusMap = {
      S1: { Week: 2, Total: 10, WeekKey: "2026-11-09", ReadAt: "old" },
    };
    const snap = JSON.parse(JSON.stringify(before));
    const after = incrementAfterSuccess(before, "S1", now);
    expect(before).toEqual(snap);
    expect(after).not.toBe(before);
    expect(after.S1).not.toBe(before.S1);
  });
});

describe("parseStatusMap", () => {
  it("parses a map, leniently defaulting missing fields", () => {
    const m = parseStatusMap('{"S1":{"Week":1,"Total":3}}');
    expect(m.S1).toEqual({ Week: 1, Total: 3, WeekKey: "", ReadAt: "" });
  });

  it("throws on malformed JSON or a non-object top level", () => {
    expect(() => parseStatusMap("{bad")).toThrow(Error);
    expect(() => parseStatusMap('"str"')).toThrow(Error);
    expect(() => parseStatusMap("[1,2]")).toThrow(Error);
    expect(() => parseStatusMap("null")).toThrow(Error);
  });
});

describe("serializeStatusMap", () => {
  it("emits 2-space JSON and roundtrips", () => {
    const m: ExerciseStatusMap = { S1: { Week: 1, Total: 3, WeekKey: "k", ReadAt: "r" } };
    const s = serializeStatusMap(m);
    expect(s).toBe(
      "{\n  \"S1\": {\n    \"Week\": 1,\n    \"Total\": 3,\n    \"WeekKey\": \"k\",\n    \"ReadAt\": \"r\"\n  }\n}",
    );
    expect(s.charCodeAt(0)).not.toBe(0xfeff); // 无 BOM
    expect(parseStatusMap(s)).toEqual(m);
    expect(serializeStatusMap({})).toBe("{}");
  });
});
