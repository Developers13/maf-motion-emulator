import { describe, expect, it } from "vitest";
import {
  parseUserList,
  serializeUserList,
  normalizeRow,
  distanceIsValid,
} from "./userlist";

describe("parseUserList", () => {
  it("preserves fields and keeps numeric distance", () => {
    const rows = parseUserList(
      `[{"student_id":"S1","password":"p","distance_km":3.2}]`,
    );
    expect(rows).toEqual([{ student_id: "S1", password: "p", distance_km: 3.2 }]);
  });

  it("defaults missing / non-string / non-finite fields leniently", () => {
    const rows = parseUserList(
      `[{"password":"p"},{"distance_km":null},{"student_id":"X","distance_km":"bad"}]`,
    );
    expect(rows).toEqual([
      { student_id: "", password: "p", distance_km: null },
      { student_id: "", password: "", distance_km: null },
      { student_id: "X", password: "", distance_km: null },
    ]);
  });

  it("throws Error for a non-array top level", () => {
    expect(() => parseUserList('{"a":1}')).toThrow(Error);
    expect(() => parseUserList('"not array"')).toThrow(Error);
    expect(() => parseUserList("123")).toThrow(Error);
  });

  it("throws Error for malformed JSON", () => {
    expect(() => parseUserList("{bad json")).toThrow(Error);
  });
});

describe("serializeUserList", () => {
  it("emits 2-space JSON in student_id/password/distance_km order", () => {
    const out = serializeUserList([
      { student_id: "S1", password: "p", distance_km: 2.4 },
      { student_id: "S2", password: "q", distance_km: null },
    ]);
    const expected = [
      "[",
      "  {",
      '    "student_id": "S1",',
      '    "password": "p",',
      '    "distance_km": 2.4',
      "  },",
      "  {",
      '    "student_id": "S2",',
      '    "password": "q"',
      "  }",
      "]",
    ].join("\n");
    expect(out).toBe(expected);
    // 字段顺序：student_id 先于 password 先于 distance_km
    expect(out.indexOf('"student_id"')).toBeLessThan(out.indexOf('"password"'));
    expect(out.indexOf('"password"')).toBeLessThan(out.indexOf('"distance_km"'));
    // distance_km 为 null 时被省略
    expect(out.split('"S2"')[1]).not.toContain("distance_km");
  });

  it("emits [] for an empty list", () => {
    expect(serializeUserList([])).toBe("[]");
  });

  it("round-trips parse/serialize for valid rows", () => {
    const rows = [
      { student_id: "A", password: "a", distance_km: 3.2 },
      { student_id: "B", password: "b", distance_km: null },
    ];
    const parsed = parseUserList(serializeUserList(rows));
    expect(parsed).toEqual(rows);
  });
});

describe("normalizeRow", () => {
  it("drops an entirely empty row (whitespace-only counts as empty)", () => {
    expect(normalizeRow("", "", "")).toEqual({ row: null, error: null });
    expect(normalizeRow("   ", "   ", "   ")).toEqual({ row: null, error: null });
  });

  it("errors when student id is empty but other fields are present", () => {
    expect(normalizeRow("", "pw", "")).toEqual({ row: null, error: "学号不能为空" });
    expect(normalizeRow("  ", "", "3.2")).toEqual({ row: null, error: "学号不能为空" });
    expect(normalizeRow("", "", "")).toHaveProperty("error", null);
  });

  it("accepts 2.4 / 3.2 and maps empty distance to null", () => {
    expect(normalizeRow("S1", "p", "3.2")).toEqual({
      row: { student_id: "S1", password: "p", distance_km: 3.2 },
      error: null,
    });
    expect(normalizeRow("S1", "p", "2.4")).toEqual({
      row: { student_id: "S1", password: "p", distance_km: 2.4 },
      error: null,
    });
    expect(normalizeRow("S1", "p", "  ")).toEqual({
      row: { student_id: "S1", password: "p", distance_km: null },
      error: null,
    });
  });

  it("accepts the 0.001 float boundary and rejects out-of-range / non-numeric", () => {
    expect(normalizeRow("S1", "p", "3.199")).toEqual({
      row: { student_id: "S1", password: "p", distance_km: 3.199 },
      error: null,
    });
    expect(normalizeRow("S1", "p", "5")).toEqual({
      row: null,
      error: "距离只能填 2.4 或 3.2",
    });
    expect(normalizeRow("S1", "p", "2.3")).toEqual({
      row: null,
      error: "距离只能填 2.4 或 3.2",
    });
    expect(normalizeRow("S1", "p", "abc")).toEqual({
      row: null,
      error: "距离只能填 2.4 或 3.2",
    });
  });

  it("trims student_id but leaves password untouched", () => {
    const r = normalizeRow("  S1  ", "  pw  ", "3.2");
    expect(r.row).toEqual({ student_id: "S1", password: "  pw  ", distance_km: 3.2 });
  });
});

describe("distanceIsValid", () => {
  it("true for 2.4 / 3.2 (incl. surrounding spaces, boundary), false otherwise", () => {
    expect(distanceIsValid("2.4")).toBe(true);
    expect(distanceIsValid("3.2")).toBe(true);
    expect(distanceIsValid(" 3.2 ")).toBe(true);
    expect(distanceIsValid("2.40")).toBe(true);
    expect(distanceIsValid("3.199")).toBe(true);
    expect(distanceIsValid("")).toBe(false);
    expect(distanceIsValid("   ")).toBe(false);
    expect(distanceIsValid("5")).toBe(false);
    expect(distanceIsValid("abc")).toBe(false);
    expect(distanceIsValid("2.3")).toBe(false);
  });
});
