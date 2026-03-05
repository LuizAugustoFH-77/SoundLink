package com.soundlink.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

private val DarkColorScheme = darkColorScheme(
    primary = Color(0xFF5ED8C8),
    onPrimary = Color(0xFF062520),
    primaryContainer = Color(0xFF123B37),
    onPrimaryContainer = Color(0xFFB6FFF4),
    secondary = Color(0xFFF2B673),
    onSecondary = Color(0xFF3B2100),
    tertiary = Color(0xFF8FCBFF),
    background = Color(0xFF081218),
    onBackground = Color(0xFFE8F2F4),
    surface = Color(0xFF101A21),
    onSurface = Color(0xFFE8F2F4),
    surfaceVariant = Color(0xFF16262F),
    onSurfaceVariant = Color(0xFFA9BEC4),
    outline = Color(0xFF2D4953),
    error = Color(0xFFFF8E86),
    errorContainer = Color(0xFF5B1F1A),
    onErrorContainer = Color(0xFFFFDAD6)
)

private val LightColorScheme = lightColorScheme(
    primary = Color(0xFF006B61),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF8DF8E8),
    onPrimaryContainer = Color(0xFF00201C),
    secondary = Color(0xFF8A4B08),
    onSecondary = Color.White,
    tertiary = Color(0xFF005D92),
    background = Color(0xFFF4FAFB),
    onBackground = Color(0xFF152126),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF152126),
    surfaceVariant = Color(0xFFD8E7EB),
    onSurfaceVariant = Color(0xFF3E5158),
    outline = Color(0xFF6C848C),
    error = Color(0xFFBA1A1A),
    errorContainer = Color(0xFFFFDAD6),
    onErrorContainer = Color(0xFF410002)
)

private val SoundLinkTypography = Typography(
    displayLarge = TextStyle(
        fontWeight = FontWeight.Black,
        fontSize = 42.sp,
        lineHeight = 46.sp,
        letterSpacing = (-1.2).sp
    ),
    headlineLarge = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 30.sp,
        lineHeight = 34.sp,
        letterSpacing = (-0.6).sp
    ),
    headlineMedium = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 24.sp,
        lineHeight = 28.sp,
        letterSpacing = (-0.4).sp
    ),
    titleLarge = TextStyle(
        fontWeight = FontWeight.SemiBold,
        fontSize = 20.sp,
        lineHeight = 24.sp
    ),
    titleMedium = TextStyle(
        fontWeight = FontWeight.SemiBold,
        fontSize = 16.sp,
        lineHeight = 20.sp
    ),
    bodyLarge = TextStyle(
        fontWeight = FontWeight.Medium,
        fontSize = 15.sp,
        lineHeight = 22.sp
    ),
    bodyMedium = TextStyle(
        fontWeight = FontWeight.Normal,
        fontSize = 14.sp,
        lineHeight = 20.sp
    ),
    labelLarge = TextStyle(
        fontWeight = FontWeight.SemiBold,
        fontSize = 13.sp,
        lineHeight = 16.sp,
        letterSpacing = 0.3.sp
    )
)

@Composable
fun SoundLinkTheme(
    darkTheme: Boolean = true,
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = if (darkTheme) DarkColorScheme else LightColorScheme,
        typography = SoundLinkTypography,
        content = content
    )
}
