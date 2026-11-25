-- Migración 10: repara las secciones que la migración 9 convirtió desde SongSlides para que se puedan GUARDAR con las reglas del módulo (etiquetas únicas en forma
-- canónica, sin «[ ] , ;», ≤ 60 caracteres; sin secciones vacías; sin líneas con forma de etiqueta u «Orden:»; tipo derivado del nombre).
-- Esas reglas viven en código (SectionAliases.CanonicalLabel / Key, SongValidator): el trabajo lo hace el paso de código `SongSectionRepair` (Core: SongLabelRepair),
-- que el MigrationRunner ejecuta DENTRO de esta misma transacción, justo después de este script. No cambia el esquema. Aplicada a datos ya válidos no cambia nada.
-- Una migración publicada no se edita nunca: este archivo y su paso de código quedan fijos.
SELECT 1;