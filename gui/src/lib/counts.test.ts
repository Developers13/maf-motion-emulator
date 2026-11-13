import { describe, expect, it } from "vitest";
import { parseCountsLine, parseSkipLine } from "./counts";

describe("parseCountsLine", () => {
  it("parses a valid counts line", () => {
    expect(parseCountsLine("[e2e] COUNTS sid=S1 week=2 total=10")).toEqual({
      sid: "S1",
      week: 2,
      total: 10,
    });
  });

  it("allows leading whitespace", () => {
    expect(parseCountsLine("   [e2e] COUNTS sid=S1 week=1 total=0")).toEqual({
      sid: "S1",
      week: 1,
      total: 0,
    });
  });

  it("returns null when week or total is negative", () => {
    expect(parseCountsLine("[e2e] COUNTS sid=S1 week=-1 total=10")).toBeNull();
    expect(parseCountsLine("[e2e] COUNTS sid=S1 week=1 total=-1")).toBeNull();
    expect(parseCountsLine("[e2e] COUNTS sid=S1 week=-1 total=-2")).toBeNull();
  });

  it("returns null for malformed or non-matching lines", () => {
    expect(parseCountsLine("[e2e] COUNTS sid=S1 week=2")).toBeNull(); // missing total
    expect(parseCountsLine("[e2e] SKIP sid=S1")).toBeNull(); // wrong kind
    expect(parseCountsLine("hello")).toBeNull();
    expect(parseCountsLine("[e2e] COUNTS week=2 total=10")).toBeNull(); // missing sid
    expect(parseCountsLine("[e2e] COUNTS sid= week=2 total=10")).toBeNull(); // sid empty
  });
});

describe("parseSkipLine", () => {
  it("returns the sid for a valid skip line", () => {
    expect(parseSkipLine("[e2e] SKIP sid=S1")).toBe("S1");
    expect(parseSkipLine("  [e2e] SKIP sid=S2")).toBe("S2");
  });

  it("returns null for malformed or non-matching lines", () => {
    expect(parseSkipLine("[e2e] COUNTS sid=S1 week=1 total=1")).toBeNull();
    expect(parseSkipLine("hello")).toBeNull();
    expect(parseSkipLine("[e2e] SKIP")).toBeNull();
  });
});
