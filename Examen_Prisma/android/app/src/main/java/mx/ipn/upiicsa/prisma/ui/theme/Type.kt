// Tipografía de la app ABCO: la de Material 3 por defecto más un estilo monoespaciado.
package mx.ipn.upiicsa.prisma.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.sp

/** Escala tipográfica de ABCO (nombre heredado de PRISMA-L); usa los valores por defecto de Material 3. */
val PrismaTypography = Typography()

/** Texto monoespaciado para hex y tablas, donde los caracteres deben quedar alineados en columnas. */
val MonoStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 13.sp, lineHeight = 18.sp)
