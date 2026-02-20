shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back;

uniform vec4 water_color : source_color = vec4(0.05, 0.2, 0.3, 0.7);
uniform float wave_speed : hint_range(0.0, 5.0) = 1.0;
uniform float wave_scale : hint_range(0.001, 0.1) = 0.02;
uniform float wave_height : hint_range(0.0, 2.0) = 0.3;

void vertex() {
	float freq1 = VERTEX.x * wave_scale * 3.0;
	float freq2 = VERTEX.z * wave_scale * 2.3;
	VERTEX.y += sin(freq1 + TIME * wave_speed) * wave_height
	           + sin(freq2 + TIME * wave_speed * 0.7) * wave_height * 0.6;
}

void fragment() {
	float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
	ALBEDO = water_color.rgb;
	ALPHA = mix(0.75, 0.95, fresnel);
	METALLIC = 0.3;
	ROUGHNESS = 0.1;
}
