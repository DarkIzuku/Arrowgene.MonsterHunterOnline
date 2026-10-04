# Política de traducción es-ES para Monster Hunter Online

Objetivo: traducir la mayor cantidad posible de texto visible al jugador sin convertir nombres propios, marcas ni términos canónicos en traducciones que suenen artificiales.

## Reglas base

1. **UI, mensajes, descripciones y objetivos:** traducir al español natural.
2. **Nombres propios de monstruos, lugares, personajes y marcas:** conservar el nombre oficial/canónico cuando exista.
3. **Terminología oficial de Monster Hunter:** usar la nomenclatura española de Capcom cuando exista.
4. **Nombres MHO sin localización oficial clara:** preferir el nombre establecido por el parche inglés o una transliteración estable antes que una traducción literal rara.
5. **Marcas/plataformas:** conservar el nombre de marca (QQ, WeGame, MHO, VIP, etc.) y traducir solo el texto que lo rodea.
6. **Fuentes, nombres de símbolos, nombres de clases ActionScript, IDs y strings de debug:** no traducir.
7. **Placeholders y formato:** conservar exactamente tokens como %s, %d, {0}, $variables, tags HTML/Scaleform y rutas internas.
8. **Términos ambiguos:** marcar `review` en vez de fijar una traducción dudosa.

## Terminología canónica fijada

Estas formas se toman como base para mantener consistencia con las localizaciones oficiales de Monster Hunter:

- 大剑 -> Gran espada
- 太刀 -> Espada larga
- 片手 -> Espada y escudo
- 双刀 -> Espadas dobles
- 锤子 -> Martillo
- 笛子 / 狩猎笛 -> Cornamusa
- 长枪 -> Lanza
- 铳枪 -> Lanza pistola
- 斩斧 / 斩击斧 -> Hacha espada
- 弓 -> Arco
- 弩 -> Ballesta
- 猎人卡片 -> Tarjeta del gremio
- 艾露猫 -> Camarada Felyne (o "camarada" cuando el contexto ya deja claro que es Felyne)
- 猎团 -> Gremio
- 麻痹陷阱 -> Trampa de choque

## Convenciones MHO

Para evitar que distintos sistemas terminen con el mismo nombre:

- 队伍 -> Grupo
- 战队 -> Escuadrón
- 猎团 -> Gremio
- 团队 (8/12/16 jugadores) -> Grupo de X
- 猎友 -> Amigo

Las cadenas relacionadas con `组团` quedan en revisión hasta confirmar si corresponden a grupo normal, banda/raid o una capa separada del sistema multijugador.

## Estados del catálogo

- `translate`: traducción aprobada para esta primera pasada.
- `preserve`: mantener nombre/marca; traducir solo contexto externo si lo hay.
- `ignore`: string técnico, fuente, debug o recurso interno.
- `review`: contexto insuficiente o posible nombre propio.

## Prioridad

1. UI/SWF
2. Mensajes del sistema
3. NPC/monstruos/mapas
4. Misiones
5. Habilidades/buffs
6. Nombres de objetos/equipo
7. Descripciones
8. Crafting/eventos/contenido secundario

El objetivo final es traducir lo máximo posible, no solo el bloque UI inicial.
