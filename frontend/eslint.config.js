import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist", "node_modules"] },
  ...tseslint.configs.strictTypeChecked,
  {
    files: ["src/**/*.{ts,tsx}", "vite.config.ts"],
    languageOptions: { parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname } },
    rules: { "@typescript-eslint/no-explicit-any": "error" },
  },
  { files: ["eslint.config.js"], ...tseslint.configs.disableTypeChecked },
);
