import { describe, expect, it } from "vitest";
import { CHARACTERS, characterName, getCharacter } from "./types";

describe("standalone character identities", () => {
  it("keeps Gran and Djeeta as distinct captain hashes", () => {
    expect(CHARACTERS).toHaveLength(29);
    expect(new Set(CHARACTERS.map((character) => character.hash)).size).toBe(29);

    expect(getCharacter(0x2a26b1b2)).toEqual({
      hash: 0x2a26b1b2,
      zh: "古兰",
      en: "Gran",
    });
    expect(getCharacter(0xa4acba76)).toEqual({
      hash: 0xa4acba76,
      zh: "姬塔",
      en: "Djeeta",
    });
    expect(characterName(0x2a26b1b2, "zh-CN")).toBe("古兰");
    expect(characterName(0xa4acba76, "en")).toBe("Djeeta");
  });
});
