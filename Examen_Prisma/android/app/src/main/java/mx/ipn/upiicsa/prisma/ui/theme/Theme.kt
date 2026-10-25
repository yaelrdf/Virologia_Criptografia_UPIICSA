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

/** Colores de estado que Material 3 no define (p. ej. "sí" en el comparador). */
@Immutable
data class StatusColors(val success: Color)

private val LightStatus = StatusColors(success = GreenLight)
private val DarkStatus = StatusColors(success = GreenDark)

private val LocalStatusColors = staticCompositionLocalOf { LightStatus }

/** [darkTheme] lo decide MainActivity según el modo elegido (automático, claro u oscuro). */
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

/** Verde de "sí" del tema activo. */
val successColor: Color
    @Composable @ReadOnlyComposable
    get() = LocalStatusColors.current.success
