// Utilidades compartidas por los colectores.
/** Redondea v a d decimales (por ejemplo, round(3.14159, 2) = 3.14). */
export const round = (v, d) => Math.round(v * 10 ** d) / 10 ** d;
