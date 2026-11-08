// Tema Material 3 de ABCO: esquemas de color claro y oscuro, y colores de estado adicionales.
package mx.ipn.upiicsa.prisma.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/** Esquema de color claro construido con la paleta de Color.kt. */
private val LightColors = lightColorScheme(
    primary = GreenLight, onPrimary = Color.White,
    primaryContainer = GreenContainerLight, onPrimaryContainer = OnGreenContainerLight,
    inversePrimary = GreenDark,
    secondary = SlateLight, onSecondary = Color.White,
    secondaryContainer = SlateContainerLight, onSecondaryContainer = OnSlateContainerLight,
    tertiary = AmberLight, onTertiary = Color.White,
    tertiaryContainer = AmberContainerLight, onTertiaryContainer = OnAmberContainerLight,
    error = ErrorLight, onError = Color.White,
    errorContainer = ErrorContainerLight, onErrorContainer = OnErrorContainerLight,
    background = BackgroundLight, onBackground = OnSurfaceLight,
    surface = SurfaceLight, onSurface = OnSurfaceLight,
    surfaceVariant = SurfaceVariantLight, onSurfaceVariant = OnSurfaceVariantLight,
    surfaceTint = GreenLight,
    surfaceBright = SurfaceLight, surfaceDim = ContainerHighestLight,
    surfaceContainerLowest = ContainerLowestLight,
    surfaceContainerLow = ContainerLowLight,
    surfaceContainer = ContainerLight,
    surfaceContainerHigh = ContainerHighLight,
    surfaceContainerHighest = ContainerHighestLight,
    inverseSurface = Color(0xFF2E3336), inverseOnSurface = Color(0xFFEFF2F4),
    outline = OutlineLight, outlineVariant = OutlineVariantLight,
)

/**
 * Esquema de color oscuro. `surface` usa el mismo gris que el fondo para que las tarjetas se distingan
 * por los niveles `surfaceContainer*` y no por un salto de color.
 */
private val DarkColors = darkColorScheme(
    primary = GreenDark, onPrimary = OnGreenDark,
    primaryContainer = GreenContainerDark, onPrimaryContainer = OnGreenContainerDark,
    inversePrimary = GreenLight,
    secondary = SlateDark, onSecondary = OnSlateDark,
    secondaryContainer = SlateContainerDark, onSecondaryContainer = OnSlateContainerDark,
    tertiary = AmberDark, onTertiary = OnAmberDark,
    tertiaryContainer = AmberContainerDark, onTertiaryContainer = OnAmberContainerDark,
    error = ErrorDark, onError = OnErrorDark,
    errorContainer = ErrorContainerDark, onErrorContainer = OnErrorContainerDark,
    background = BackgroundDark, onBackground = OnSurfaceDark,
    surface = BackgroundDark, onSurface = OnSurfaceDark,
    surfaceVariant = SurfaceVariantDark, onSurfaceVariant = OnSurfaceVariantDark,
    surfaceTint = GreenDark,
    surfaceBright = ContainerHighestDark, surfaceDim = BackgroundDark,
    surfaceContainerLowest = ContainerLowestDark,
    surfaceContainerLow = ContainerLowDark,
    surfaceContainer = ContainerDark,
    surfaceContainerHigh = ContainerHighDark,
    surfaceContainerHighest = ContainerHighestDark,
    inverseSurface = OnSurfaceDark, inverseOnSurface = Color(0xFF2E3336),
    outline = OutlineDark, outlineVariant = OutlineVariantDark,
)

/**
 * Colores de estado que Material 3 no define (p. ej. "sí" en el comparador).
 *
 * @property success verde con contraste suficiente para el tema activo.
 */
@Immutable
data class StatusColors(val success: Color)

/** Colores de estado para el tema claro. */
private val LightStatus = StatusColors(success = GreenLight)
/** Colores de estado para el tema oscuro. */
private val DarkStatus = StatusColors(success = GreenDark)

/**
 * CompositionLocal con los [StatusColors] del tema activo. Es `static` porque solo cambia al cambiar de
 * tema, y entonces conviene recomponer todo el árbol.
 */
private val LocalStatusColors = staticCompositionLocalOf { LightStatus }

/**
 * Tema de ABCO (el nombre `PrismaTheme` se conserva de PRISMA-L): aplica el esquema de color, la
 * tipografía y los [StatusColors] correspondientes.
 *
 * @param darkTheme lo decide MainActivity según el modo elegido (automático, claro u oscuro).
 * @param content contenido Compose al que se aplica el tema.
 */
@Composable
fun PrismaTheme(darkTheme: Boolean, content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalStatusColors provides if (darkTheme) DarkStatus else LightStatus) {
        MaterialTheme(
            colorScheme = if (darkTheme) DarkColors else LightColors,
            typography = PrismaTypography,
            content = content,
        )
    }
}

/** Verde de "sí" del tema activo; se lee desde cualquier Composable sin pasarlo como parámetro. */
val successColor: Color
    @Composable @ReadOnlyComposable
    get() = LocalStatusColors.current.success
