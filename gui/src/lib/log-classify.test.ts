import { describe, expect, it } from "vitest";
import { classifyLogLine } from "./log-classify";

describe("classifyLogLine", () => {
  it("banner for lines starting with ====", () => {
    expect(classifyLogLine("=== Ready ===")).toBe("banner");
    expect(classifyLogLine("===")).toBe("banner");
  });

  it("ok when the line contains '-> OK'", () => {
    expect(classifyLogLine("login -> OK")).toBe("ok");
    expect(classifyLogLine("[gui] done -> OK")).toBe("ok");
  });

  it("error when the line contains 'FAILED'", () => {
    expect(classifyLogLine("step FAILED")).toBe("error");
  });

  it("error when isError is true, even for a plain line", () => {
    expect(classifyLogLine("plain line", true)).toBe("error");
  });

  it("error via [mumu] ERROR / [adb-prep] FAIL markers", () => {
    expect(classifyLogLine("[mumu] ERROR no instance")).toBe("error");
    expect(classifyLogLine("[adb-prep] FAIL")).toBe("error");
  });

  it("info when the line starts with '['; anything else is default", () => {
    expect(classifyLogLine("[gui] msg")).toBe("info");
    expect(classifyLogLine("hello")).toBe("default");
    expect(classifyLogLine("  indented")).toBe("default");
  });

  it("applies priority banner > ok > error > info", () => {
    expect(classifyLogLine("=== FAILED ===", true)).toBe("banner");
    expect(classifyLogLine("[gui] ok -> OK")).toBe("ok");
    expect(classifyLogLine("[gui] FAILED")).toBe("error");
  });
});
