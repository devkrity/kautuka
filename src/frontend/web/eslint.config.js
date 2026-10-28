import typescriptEslintParser from "@typescript-eslint/parser";
import typescriptEslintPlugin from "@typescript-eslint/eslint-plugin";
import secureCoding from "eslint-plugin-secure-coding";
import browserSecurity from "eslint-plugin-browser-security";

export default [
  {
    ignores: ["dist", "node_modules", "*.config.js", "**/*.d.ts"],
  },
  {
    files: ["src/**/*.{ts,tsx}"],
    languageOptions: {
      parser: typescriptEslintParser,
      parserOptions: {
        ecmaVersion: 2024,
        sourceType: "module",
        ecmaFeatures: {
          jsx: true,
        },
        project: ["tsconfig.json"],
      },
    },
    plugins: {
      "@typescript-eslint": typescriptEslintPlugin,
      "secure-coding": secureCoding,
      "browser-security": browserSecurity,
    },
    rules: {
      // Your existing rule
      "@typescript-eslint/no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["@/features/*/*/**"],
              message: "Import from feature public API only (index.ts).",
            },
          ],
        },
      ],

      // Optional: override specific secure‑coding rules
      "secure-coding/detect-object-injection": "warn",
      "secure-coding/no-hardcoded-credentials": "warn",

      // Optional: TS hygiene
      "@typescript-eslint/no-explicit-any": "warn",
      "@typescript-eslint/no-unsafe-argument": "warn",
      "@typescript-eslint/no-unsafe-member-access": "warn",

      // Browser XSS-style protection
      "browser-security/no-innerhtml": "warn",
    },
  },
  {
    files: ["src/test/**/*.{ts,tsx}"],
    rules: {
      "@typescript-eslint/no-restricted-imports": "off",
    },
  },
];
